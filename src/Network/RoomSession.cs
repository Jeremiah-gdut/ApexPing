using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ApexPing;

internal sealed record RoomMember(string Peer, string Name, string Legend, string Route, bool Paused)
{
    public string Label => $"{Name} · {Route}" + (Paused ? " · 已暂停" : "");
}
internal sealed record RoomState(string Code, string Status, RoomMember[] Members);
internal sealed record SharedPing(string Ping, int Display, double X, double Y, string Name, string Legend);
internal sealed record RoomOptions
{
    public string[] RelayUrls { get; init; } = NostrRelay.DefaultUrls;
    public string[] StunServers { get; init; } = new[] { "stun.cloudflare.com:3478", "stun.l.google.com:19302" };
    public bool Direct { get; init; } = true;
    public int DiscoveryPort { get; init; } = 47475;
}

internal sealed class RoomSession : IDisposable, IAsyncDisposable
{
    private const string FullNotice = "房间最多支持 8 人，请创建另一个房间。";
    private sealed class Member
    {
        public required string Peer;
        public string Name = "玩家", Legend = "";
        public bool Paused, Internet;
        public long LastSeen, LastPong, LastProbe;
        public string Challenge = "";
        public IPEndPoint? Direct, Observed;
        public readonly HashSet<IPEndPoint> Candidates = new();
        public readonly HashSet<IPEndPoint> LanAddresses = new();
        public readonly ReplayWindow Replay = new();
        public readonly PacketBudget Pings = new(5, 10);
    }

    private readonly RoomOptions options;
    private readonly RoomKeys keys;
    private readonly NostrIdentity identity = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<Action> inbox = Channel.CreateBounded<Action>(new BoundedChannelOptions(512)
    {
        SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly Dictionary<string, Member> members = new();
    private readonly Dictionary<string, long> departed = new();
    private readonly HashSet<string> eventsSeen = new();
    private readonly Queue<string> eventOrder = new();
    private readonly Dictionary<string, (IPEndPoint Server, byte[] Request, long Sent)> stunRequests = new();
    private readonly Dictionary<string, (IPEndPoint Address, long Seen)> publicAddresses = new();
    private readonly PacketBudget incoming = new(300, 400), outgoingPings = new(5, 10), relayBudget = new(5, 8);
    private readonly List<Task> tasks = new();
    private readonly List<NostrRelay> relays = new();
    private UdpClient? udp4, udp6, discovery;
    private Task worker = Task.CompletedTask;
    private string name, legend, lastState = "", notice = "";
    private bool enabled, started;
    private readonly bool internet;
    private long seq, lastLanPresence, lastRelayPresence, lastInterfaces, startedAt;
    private string[] localCandidates = Array.Empty<string>();
    private IPAddress[] broadcasts = Array.Empty<IPAddress>();
    private int disposed;
    public string Code { get; }
    public event Action<RoomState>? Changed;
    public event Action<SharedPing>? Received;

    public RoomSession(string code, string name, string legend, bool enabled, bool internet, RoomOptions? options = null)
    {
        Code = RoomKeys.NormalizeCode(code); keys = new(Code);
        this.name = RoomKeys.CleanName(name); this.legend = legend; this.enabled = enabled; this.internet = internet;
        this.options = options ?? new();
    }

    public void Start()
    {
        if (started || stop.IsCancellationRequested) return;
        started = true; startedAt = Environment.TickCount64;
        if (options.Direct)
        {
            try
            {
                udp4 = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
                udp4.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                tasks.Add(ReceiveUdp(udp4));
            }
            catch (SocketException) { udp4?.Dispose(); udp4 = null; }
            if (Socket.OSSupportsIPv6)
            {
                try
                {
                    udp6 = new UdpClient(AddressFamily.InterNetworkV6);
                    udp6.Client.DualMode = false;
                    udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, udp4 == null ? 0 : ((IPEndPoint)udp4.Client.LocalEndPoint!).Port));
                    tasks.Add(ReceiveUdp(udp6));
                }
                catch (SocketException) { udp6?.Dispose(); udp6 = null; }
            }
            if (udp4 != null && options.DiscoveryPort > 0)
            {
                try
                {
                    discovery = new UdpClient(AddressFamily.InterNetwork);
                    discovery.Client.ExclusiveAddressUse = false;
                    discovery.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                    discovery.Client.Bind(new IPEndPoint(IPAddress.Any, options.DiscoveryPort));
                    tasks.Add(ReceiveUdp(discovery));
                }
                catch (SocketException) { discovery?.Dispose(); discovery = null; notice = "局域网发现不可用，仍会尝试公网连接。"; }
            }
        }
        if (internet)
        {
            foreach (string url in options.RelayUrls)
            {
                var relay = new NostrRelay(url, keys.Topic,
                    (source, message, epoch) => Post(() => OnRelay(source, message, epoch)), () => Post(() => { RelayPresence(); PublishState(); }));
                relays.Add(relay); tasks.Add(relay.RunAsync(stop.Token));
            }
            if (udp4 != null) tasks.Add(DiscoverPublic());
        }
        tasks.Add(Tick());
        worker = Task.Run(Process);
        Post(() => { ReadInterfaces(); AnnounceLan(); RelayPresence(); PublishState(); });
    }

    private void Post(Action action) { if (!stop.IsCancellationRequested) inbox.Writer.TryWrite(action); }

    private async Task Process()
    {
        try
        {
            await foreach (var action in inbox.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false)) action();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            Changed?.Invoke(new(Code, "联机已停止，请退出房间后重试：" + error.Message, Array.Empty<RoomMember>()));
            Dispose();
        }
        finally { keys.Dispose(); identity.Dispose(); }
    }

    public void UpdateProfile(string newName, string newLegend, bool newEnabled)
    {
        Post(() =>
        {
            string cleaned = RoomKeys.CleanName(newName);
            if (name == cleaned && legend == newLegend && enabled == newEnabled) return;
            name = cleaned; legend = newLegend; enabled = newEnabled;
            lastLanPresence = lastRelayPresence = 0;
            AnnounceLan(); RelayPresence(); PublishState();
        });
    }

    public void SendPing(string ping, int display, double x, double y)
    {
        if (!Pings.All.Any(p => p.Id == ping) || display is < 1 or > 16 || !double.IsFinite(x) || !double.IsFinite(y)) return;
        Post(() =>
        {
            if (!enabled || !outgoingPings.Allow()) return;
            byte[] packet = Seal(new() { Type = "ping", Ping = ping, Display = display, X = Math.Clamp(x, 0, 1), Y = Math.Clamp(y, 0, 1) });
            bool fallback = false;
            foreach (var member in members.Values)
            {
                if (!DirectAlive(member) || !SendUdp(packet, member.Direct!)) { member.LastPong = 0; fallback = true; }
            }
            if (fallback) PublishRelay(packet);
        });
    }

    private byte[] Seal(RoomMessage message) => keys.Seal(message with
    {
        Peer = identity.PublicKey, Seq = ++seq, Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
    });

    private byte[] Presence() => Seal(new()
    {
        Type = "presence", Name = name, Legend = legend, Paused = !enabled,
        Candidates = localCandidates.Concat(publicAddresses.Values.Where(p => Environment.TickCount64 - p.Seen < 60000).Select(p => p.Address.ToString()))
            .Distinct().Take(8).ToArray()
    });

    private void AnnounceLan()
    {
        if (udp4 == null && udp6 == null) return;
        byte[] packet = Presence();
        if (discovery != null) foreach (var address in broadcasts) SendUdp(packet, new(address, options.DiscoveryPort));
        foreach (var peer in members.Values) if (peer.Direct != null) SendUdp(packet, peer.Direct);
        lastLanPresence = Environment.TickCount64;
    }

    private void RelayPresence()
    {
        if (!internet || Environment.TickCount64 - lastRelayPresence < 1000) return;
        if (PublishRelay(Presence())) lastRelayPresence = Environment.TickCount64;
    }

    private bool PublishRelay(byte[] packet)
    {
        if (relays.Count == 0 || !relayBudget.Allow()) return false;
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(new object[] { "EVENT", identity.Sign(packet, keys.Topic) }, RoomKeys.Json);
        return relays.Count(r => r.Publish(json)) > 0;
    }

    private void OnRelay(NostrRelay relay, NostrEvent message, int epoch)
    {
        if (relay.Epoch != epoch) return;
        if (!incoming.Allow() || !RoomKeys.Hex(message.Id, 64) || message.Content == null || message.Content.Length > 1600) return;
        if (message.Pubkey != identity.PublicKey && eventsSeen.Contains(message.Id)) return;
        byte[] packet;
        try { packet = Convert.FromBase64String(message.Content); }
        catch (FormatException) { return; }
        var plain = keys.Open(packet);
        if (plain == null || plain.Peer != message.Pubkey || !NostrIdentity.Verify(message, keys.Topic)) return;
        if (plain.Peer == identity.PublicKey) { relay.ConfirmDelivery(epoch); return; }
        eventsSeen.Add(message.Id); eventOrder.Enqueue(message.Id);
        if (eventOrder.Count > 512) eventsSeen.Remove(eventOrder.Dequeue());
        OnMessage(plain, null);
    }

    private async Task ReceiveUdp(UdpClient socket)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                var packet = await socket.ReceiveAsync(stop.Token).ConfigureAwait(false);
                if (packet.Buffer.Length is >= 20 and <= RoomKeys.MaxPacket) Post(() => OnUdp(packet.Buffer, packet.RemoteEndPoint, ReferenceEquals(socket, discovery)));
            }
            catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException) { break; }
            catch (SocketException error)
            {
                if (stop.IsCancellationRequested) break;
                // Windows reports an unreachable UDP peer on Receive; that must not kill every other peer's socket.
                if (error.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused) continue;
                Post(() => { notice = "UDP 连接受限，公网标记将使用中继。"; PublishState(); });
                try { await Task.Delay(250, stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void OnUdp(byte[] packet, IPEndPoint from, bool lanDiscovery)
    {
        if (!incoming.Allow()) return;
        if (packet[0] == 1 && packet[1] == 1 && BinaryPrimitives.ReadUInt32BigEndian(packet.AsSpan(4)) == 0x2112a442)
        {
            string transaction = Convert.ToHexString(packet.AsSpan(8, 12));
            if (!stunRequests.TryGetValue(transaction, out var pending) || !from.Equals(pending.Server) || Environment.TickCount64 - pending.Sent > 8000) return;
            stunRequests.Remove(transaction);
            var endpoint = Stun.Read(packet, pending.Request.AsSpan(8));
            if (endpoint != null) { publicAddresses[from.ToString()] = (endpoint, Environment.TickCount64); lastRelayPresence = 0; RelayPresence(); }
            return;
        }
        var message = keys.Open(packet);
        if (message != null) OnMessage(message, from, lanDiscovery);
    }

    private void OnMessage(RoomMessage message, IPEndPoint? from, bool lanDiscovery = false)
    {
        long now = Environment.TickCount64;
        if (message.Peer == identity.PublicKey || departed.ContainsKey(message.Peer)
            || Math.Abs((double)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - message.Time) > 120000) return;
        bool isNew = !members.TryGetValue(message.Peer, out var member);
        if (isNew)
        {
            if (message.Type != "presence") return;
            if (members.Count >= 7) { notice = FullNotice; PublishState(); return; }
            member = new Member { Peer = message.Peer };
            members.Add(message.Peer, member);
        }
        if (!member!.Replay.Accept(message.Seq)) return;
        member.LastSeen = now;
        if (from != null && RoomKeys.Endpoint(from.ToString()) != null) member.Observed = from;
        if (from == null) member.Internet = true;
        switch (message.Type)
        {
            case "presence":
                member.Name = RoomKeys.CleanName(message.Name); member.Legend = message.Legend!; member.Paused = message.Paused;
                member.Candidates.Clear();
                foreach (string candidate in message.Candidates!)
                {
                    var endpoint = RoomKeys.Endpoint(candidate)!;
                    if (internet || IsLocal(endpoint.Address)) member.Candidates.Add(endpoint);
                }
                if (from != null && RoomKeys.Endpoint(from.ToString()) != null) member.Candidates.Add(from);
                if (lanDiscovery && from != null)
                {
                    if (member.LanAddresses.Count >= 8) member.LanAddresses.Clear();
                    member.LanAddresses.Add(from);
                }
                if (isNew)
                {
                    Probe(member);
                    if (from != null) SendUdp(Presence(), from);
                    else { lastRelayPresence = 0; RelayPresence(); }
                }
                break;
            case "probe" when message.Target == identity.PublicKey && from != null:
                SendUdp(Seal(new() { Type = "pong", Target = member.Peer, Challenge = message.Challenge }), from);
                break;
            case "pong" when message.Target == identity.PublicKey && from != null:
                if (message.Challenge == member.Challenge && now - member.LastProbe <= 6000)
                {
                    bool preferLan = (member.LanAddresses.Contains(from) || IsLocal(from.Address))
                        && member.Direct != null && !member.LanAddresses.Contains(member.Direct) && !IsLocal(member.Direct.Address);
                    if (!DirectAlive(member) || from.Equals(member.Direct) || preferLan) { member.Direct = from; member.LastPong = now; }
                }
                break;
            case "ping":
                if (enabled && !member.Paused && member.Pings.Allow()) Received?.Invoke(new(message.Ping!, message.Display, message.X, message.Y, member.Name, member.Legend));
                break;
            case "bye":
                members.Remove(member.Peer); departed[member.Peer] = now;
                break;
        }
        PublishState();
    }

    private static bool IsLocal(IPAddress ip)
    {
        byte[] bytes = ip.GetAddressBytes();
        return ip.AddressFamily == AddressFamily.InterNetwork
            ? bytes[0] == 10 || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 169 && bytes[1] == 254)
            : (bytes[0] & 0xfe) == 0xfc;
    }

    private bool DirectAlive(Member member) => options.Direct && member.Direct != null && member.LastPong > 0 && Environment.TickCount64 - member.LastPong < 8000;

    private void Probe(Member member)
    {
        if (udp4 == null && udp6 == null) return;
        member.Challenge = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        member.LastProbe = Environment.TickCount64;
        byte[] packet = Seal(new() { Type = "probe", Target = member.Peer, Challenge = member.Challenge });
        foreach (var endpoint in new[] { member.Observed, member.Direct }.Where(e => e != null).Cast<IPEndPoint>()
            .Concat(member.Candidates.OrderByDescending(e => member.LanAddresses.Contains(e) || IsLocal(e.Address))).Distinct().Take(10)) SendUdp(packet, endpoint);
    }

    private bool SendUdp(byte[] packet, IPEndPoint target)
    {
        var socket = target.AddressFamily == AddressFamily.InterNetwork ? udp4 : udp6;
        try { return socket != null && socket.Send(packet, packet.Length, target) == packet.Length; }
        catch (Exception error) when (error is SocketException or ObjectDisposedException) { return false; }
    }

    private async Task Tick()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try { while (await timer.WaitForNextTickAsync(stop.Token).ConfigureAwait(false)) Post(OnTick); }
        catch (OperationCanceledException) { }
    }

    private void OnTick()
    {
        long now = Environment.TickCount64;
        foreach (var relay in relays) relay.CheckHealth();
        if (now - lastInterfaces >= 20000) ReadInterfaces();
        if (now - lastLanPresence >= 2000) AnnounceLan();
        if (now - lastRelayPresence >= (now - startedAt < 10000 ? 2000 : 10000)) RelayPresence();
        foreach (var member in members.Values.ToArray())
        {
            if (now - member.LastSeen > (member.Internet ? 35000 : 15000)) { members.Remove(member.Peer); continue; }
            if (now - member.LastProbe >= (DirectAlive(member) ? 2500 : 1500)) Probe(member);
        }
        foreach (string peer in departed.Where(p => now - p.Value > 120000).Select(p => p.Key).ToArray()) departed.Remove(peer);
        foreach (string tx in stunRequests.Where(p => now - p.Value.Sent > 8000).Select(p => p.Key).ToArray()) stunRequests.Remove(tx);
        if (members.Count < 7 && notice == FullNotice) notice = "";
        PublishState();
    }

    private void ReadInterfaces()
    {
        var candidates = new List<string>();
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
                .OrderByDescending(n => n.GetIPProperties().GatewayAddresses.Count > 0))
            {
                foreach (var address in adapter.GetIPProperties().UnicastAddresses)
                {
                    var ip = address.Address;
                    if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal) continue;
                    var socket = ip.AddressFamily == AddressFamily.InterNetwork ? udp4 : udp6;
                    if (socket == null) continue;
                    var endpoint = new IPEndPoint(ip, ((IPEndPoint)socket.Client.LocalEndPoint!).Port);
                    if (RoomKeys.Endpoint(endpoint.ToString()) != null && (internet || IsLocal(ip))) candidates.Add(endpoint.ToString());
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        byte[] bytes = ip.GetAddressBytes(), mask = address.IPv4Mask.GetAddressBytes();
                        targets.Add(new IPAddress(bytes.Select((b, i) => (byte)(b | ~mask[i])).ToArray()));
                    }
                }
            }
        }
        catch (Exception error) when (error is NetworkInformationException or SocketException or ObjectDisposedException) { }
        // Reserve two candidate slots for NAT mappings learned through STUN.
        localCandidates = candidates.Distinct().Take(6).ToArray(); broadcasts = targets.ToArray(); lastInterfaces = Environment.TickCount64;
    }

    private async Task DiscoverPublic()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                foreach (string server in options.StunServers)
                {
                    try
                    {
                        string[] parts = server.Split(':');
                        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                        deadline.CancelAfter(TimeSpan.FromSeconds(5));
                        var addresses = await Dns.GetHostAddressesAsync(parts[0], deadline.Token).ConfigureAwait(false);
                        var ip = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                        if (ip == null) continue;
                        var target = new IPEndPoint(ip, int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
                        byte[] request = Stun.Request();
                        Post(() =>
                        {
                            stunRequests[Convert.ToHexString(request.AsSpan(8))] = (target, request, Environment.TickCount64);
                            SendUdp(request, target);
                        });
                    }
                    catch (Exception error) when (error is SocketException or OperationCanceledException) { }
                }
                await Task.Delay(30000, stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private void PublishState()
    {
        int available = relays.Count(r => r.Healthy);
        string status = internet ? $"公共中继 {available}/{relays.Count} 可用 · 优先 P2P" : "仅局域网直连";
        if (internet && available == 0) status = Environment.TickCount64 - startedAt < 15000 ? "正在连接免费公共中继…" : "公共中继暂不可用，仍会尝试局域网直连。";
        if (!string.IsNullOrEmpty(notice)) status += " · " + notice;
        var peers = new[] { new RoomMember(identity.PublicKey, name + "（我）", legend, "本机", !enabled) }
            .Concat(members.Values.OrderBy(m => m.Peer).Select(m => new RoomMember(m.Peer, m.Name, m.Legend,
                DirectAlive(m) ? m.LanAddresses.Contains(m.Direct!) || IsLocal(m.Direct!.Address) ? "局域网直连" : "P2P 直连" : internet && available > 0 ? "公共中继" : "连接中", m.Paused))).ToArray();
        string fingerprint = status + "|" + string.Join('|', peers.Select(p => p.ToString()));
        if (fingerprint == lastState) return;
        lastState = fingerprint;
        Changed?.Invoke(new(Code, status, peers));
    }

    public async ValueTask DisposeAsync()
    {
        if (!stop.IsCancellationRequested)
        {
            Post(() =>
            {
                byte[] bye = Seal(new() { Type = "bye" });
                foreach (var member in members.Values) if (member.Direct != null) SendUdp(bye, member.Direct);
                PublishRelay(bye);
            });
            await Task.Delay(200).ConfigureAwait(false);
        }
        Dispose();
        try { await Task.WhenAll(tasks.Append(worker)).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
        catch (Exception error) when (error is OperationCanceledException or TimeoutException or SocketException or ObjectDisposedException or IOException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); inbox.Writer.TryComplete();
        udp4?.Dispose(); udp6?.Dispose(); discovery?.Dispose();
        foreach (var relay in relays) relay.Dispose();
        if (!started) { keys.Dispose(); identity.Dispose(); }
    }
}
