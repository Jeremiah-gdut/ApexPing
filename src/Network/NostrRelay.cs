using System;
using System.IO;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace ApexPing;

internal sealed class NostrRelay : IDisposable
{
    internal static readonly string[] DefaultUrls =
    {
        "wss://nos.lol", "wss://nostr.mom", "wss://relay.snort.social",
        "wss://relay.agentry.com", "wss://relay.routstr.com", "wss://nostr.rblb.it"
    };
    private readonly Uri uri;
    private readonly string topic;
    private readonly Action<NostrRelay, NostrEvent, int> received;
    private readonly Action changed;
    private ClientWebSocket? socket;
    private Channel<byte[]>? outgoing;
    private volatile bool healthy;
    private long openedAt, lastEcho;
    private int epoch;
    public int Epoch => Volatile.Read(ref epoch);
    public bool Healthy => Volatile.Read(ref outgoing) != null && socket?.State == WebSocketState.Open
        && healthy && Environment.TickCount64 - Volatile.Read(ref lastEcho) < 45000;

    public NostrRelay(string url, string topic, Action<NostrRelay, NostrEvent, int> received, Action changed)
    {
        uri = new(url); this.topic = topic; this.received = received; this.changed = changed;
    }

    public bool Publish(byte[] json) => Volatile.Read(ref outgoing)?.Writer.TryWrite(json) == true;

    public void ConfirmDelivery(int generation)
    {
        if (generation != Epoch || Volatile.Read(ref outgoing) == null) return;
        Volatile.Write(ref lastEcho, Environment.TickCount64);
        if (!healthy) { healthy = true; changed(); }
    }

    public void CheckHealth()
    {
        if (Volatile.Read(ref outgoing) == null) return;
        long echo = Volatile.Read(ref lastEcho), now = Environment.TickCount64;
        if ((echo == 0 && now - Volatile.Read(ref openedAt) > 15000) || (echo != 0 && now - echo > 45000)) Dispose();
    }

    public async Task RunAsync(CancellationToken stop)
    {
        int failures = 0;
        while (!stop.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop);
            socket = ws;
            Task sender = Task.CompletedTask;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                await ws.ConnectAsync(uri, deadline.Token).ConfigureAwait(false);
                int generation = Interlocked.Increment(ref epoch);
                healthy = false;
                Volatile.Write(ref openedAt, Environment.TickCount64); Volatile.Write(ref lastEcho, 0);
                var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(32)
                {
                    SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropOldest
                });
                byte[] request = JsonSerializer.SerializeToUtf8Bytes(new object[]
                {
                    "REQ", "apexping", new System.Collections.Generic.Dictionary<string, object>
                    {
                        ["kinds"] = new[] { NostrIdentity.Kind }, ["#x"] = new[] { topic },
                        ["since"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 10, ["limit"] = 0
                    }
                }, RoomKeys.Json);
                await ws.SendAsync(request.AsMemory(), WebSocketMessageType.Text, true, lifetime.Token).ConfigureAwait(false);
                Volatile.Write(ref outgoing, channel);
                changed();
                sender = SendAsync(ws, channel, lifetime.Token);
                _ = sender.ContinueWith(_ => ws.Abort(), CancellationToken.None, TaskContinuationOptions.NotOnRanToCompletion | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                byte[] buffer = new byte[4096];
                while (!lifetime.IsCancellationRequested && ws.State == WebSocketState.Open)
                {
                    using var frame = new MemoryStream();
                    ValueWebSocketReceiveResult part;
                    do
                    {
                        part = await ws.ReceiveAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false);
                        if (part.MessageType != WebSocketMessageType.Text || frame.Length + part.Count > 8192) throw new WebSocketException("Invalid relay frame.");
                        frame.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    Read(frame.ToArray(), generation);
                    if (healthy) failures = 0;
                }
            }
            catch (Exception error) when (error is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException) { }
            finally
            {
                Interlocked.Increment(ref epoch);
                Volatile.Write(ref outgoing, null);
                healthy = false;
                lifetime.Cancel(); ws.Abort();
                try { await sender.ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException or ObjectDisposedException) { }
                changed();
            }
            if (stop.IsCancellationRequested) break;
            try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, Math.Min(++failures, 6))) + Random.Shared.NextDouble()), stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task SendAsync(ClientWebSocket ws, Channel<byte[]> channel, CancellationToken token)
    {
        await foreach (byte[] json in channel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await ws.SendAsync(json.AsMemory(), WebSocketMessageType.Text, true, deadline.Token).ConfigureAwait(false);
        }
    }

    private void Read(byte[] json, int generation)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 || root[0].ValueKind != JsonValueKind.String) return;
            string? type = root[0].GetString();
            if (type == "EVENT" && root.GetArrayLength() == 3 && root[1].ValueKind == JsonValueKind.String && root[1].GetString() == "apexping")
            {
                var message = root[2].Deserialize<NostrEvent>(RoomKeys.Json);
                if (message != null) received(this, message, generation);
            }
            // Some relays reject ephemeral kinds or require payment; no credentials or payment flow is attempted.
            else if (type == "CLOSED" && root[1].ValueKind == JsonValueKind.String && root[1].GetString() == "apexping") socket?.Abort();
            else if (type == "OK" && root.GetArrayLength() >= 3 && root[2].ValueKind == JsonValueKind.False) socket?.Abort();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException) { }
    }

    public void Dispose()
    {
        try { socket?.Abort(); }
        catch (ObjectDisposedException) { }
    }
}
