using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using NBitcoin.Secp256k1;
using SHA256 = System.Security.Cryptography.SHA256;

namespace ApexPing;

public sealed record RoomMessage
{
    public int Version { get; init; } = 1;
    public string Type { get; init; } = "";
    public string Peer { get; init; } = "";
    public long Seq { get; init; }
    public long Time { get; init; }
    public string? Name { get; init; }
    public string? Legend { get; init; }
    public bool Paused { get; init; }
    public string[]? Candidates { get; init; }
    public string? Target { get; init; }
    public string? Challenge { get; init; }
    public string? Ping { get; init; }
    public int Display { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
}

public sealed class RoomKeys : IDisposable
{
    public const int MaxPacket = 1200;
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    private static readonly byte[] Aad = Encoding.ASCII.GetBytes("ApexPing/room/v1");
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 12
    };
    private readonly byte[] key;
    public string Topic { get; }

    public RoomKeys(string code)
    {
        byte[] secret = SHA256.HashData(Encoding.ASCII.GetBytes(NormalizeCode(code)));
        key = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: Encoding.ASCII.GetBytes("ApexPing/message/v1"));
        Topic = Convert.ToHexString(HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: Encoding.ASCII.GetBytes("ApexPing/topic/v1"))).ToLowerInvariant();
        CryptographicOperations.ZeroMemory(secret);
    }

    public static string CreateCode()
    {
        string value = new(RandomNumberGenerator.GetBytes(20).Select(b => Alphabet[b & 31]).ToArray());
        return Format(value);
    }

    public static string NormalizeCode(string text)
    {
        if (text == null || text.Length > 100) throw new ArgumentException("请粘贴完整的 APEX 房间码。 ");
        string value = Regex.Replace(text.Trim().ToUpperInvariant(), "[\\s-]", "");
        if (value.StartsWith("APEX", StringComparison.Ordinal)) value = value[4..];
        if (value.Length != 20 || value.Any(c => !Alphabet.Contains(c))) throw new ArgumentException("房间码格式错误，应为 APEX-XXXXX-XXXXX-XXXXX-XXXXX。 ");
        return Format(value);
    }

    private static string Format(string value) => "APEX-" + string.Join("-", value.Chunk(5).Select(chars => new string(chars)));

    public byte[] Seal(RoomMessage message)
    {
        byte[] plain = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (plain.Length + 29 > MaxPacket) throw new ArgumentException("房间消息过大。 ");
        byte[] packet = new byte[plain.Length + 29];
        packet[0] = 1;
        RandomNumberGenerator.Fill(packet.AsSpan(1, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(packet.AsSpan(1, 12), plain, packet.AsSpan(13, plain.Length), packet.AsSpan(13 + plain.Length, 16), Aad);
        return packet;
    }

    public RoomMessage? Open(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 29 || packet.Length > MaxPacket || packet[0] != 1) return null;
        byte[] plain = new byte[packet.Length - 29];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(packet.Slice(1, 12), packet.Slice(13, plain.Length), packet[^16..], plain, Aad);
            var message = JsonSerializer.Deserialize<RoomMessage>(plain, Json);
            return message != null && Valid(message) ? message : null;
        }
        catch (Exception error) when (error is CryptographicException or JsonException or ArgumentException) { return null; }
    }

    private static bool Valid(RoomMessage m)
    {
        if (m.Version != 1 || !Hex(m.Peer, 64) || m.Seq <= 0 || m.Time <= 0) return false;
        return m.Type switch
        {
            "presence" => m.Name is { Length: <= 64 } && m.Legend != null && Regex.IsMatch(m.Legend, "^[a-z0-9_]{0,40}$")
                && m.Candidates is { Length: <= 8 } && m.Candidates.All(c => c != null && c.Length <= 80 && Endpoint(c) != null),
            "ping" => Pings.All.Any(p => p.Id == m.Ping) && m.Display is >= 1 and <= 16
                && double.IsFinite(m.X) && double.IsFinite(m.Y) && m.X is >= 0 and <= 1 && m.Y is >= 0 and <= 1,
            "probe" or "pong" => Hex(m.Target, 64) && Hex(m.Challenge, 32),
            "bye" => true,
            _ => false
        };
    }

    internal static bool Hex(string? value, int length) => value != null && value.Length == length && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    public static string CleanName(string? value)
    {
        var text = new StringBuilder();
        int count = 0;
        foreach (var rune in (value ?? "").EnumerateRunes().Take(64))
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            text.Append(rune.ToString());
            if (++count >= 16) break;
        }
        string result = text.ToString().Trim();
        return result.Length == 0 ? "玩家" : result;
    }

    public static IPEndPoint? Endpoint(string value)
    {
        if (!IPEndPoint.TryParse(value, out var endpoint) || endpoint.Port == 0) return null;
        var ip = endpoint.Address;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.Equals(IPAddress.Broadcast) || ip.IsIPv6Multicast || ip.IsIPv6LinkLocal
            || (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0)) return null;
        if (ip.AddressFamily == AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224) return null;
        return new IPEndPoint(ip, endpoint.Port);
    }

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

internal sealed class ReplayWindow
{
    private long highest;
    private ulong seen;
    public bool Accept(long seq)
    {
        if (seq <= 0) return false;
        if (seq > highest)
        {
            long shift = seq - highest;
            seen = shift >= 64 ? 1 : (seen << (int)shift) | 1;
            highest = seq;
            return true;
        }
        long offset = highest - seq;
        if (offset >= 64 || (seen & (1UL << (int)offset)) != 0) return false;
        seen |= 1UL << (int)offset;
        return true;
    }
}

internal sealed class PacketBudget
{
    private readonly double rate, capacity;
    private double tokens;
    private long last = Environment.TickCount64;
    public PacketBudget(double rate, double capacity) { this.rate = rate; this.capacity = capacity; tokens = capacity; }
    public bool Allow()
    {
        long now = Environment.TickCount64;
        tokens = Math.Min(capacity, tokens + (now - last) * rate / 1000);
        last = now;
        if (tokens < 1) return false;
        tokens--;
        return true;
    }
}

internal sealed record NostrEvent(string Id, string Pubkey, [property: JsonPropertyName("created_at")] long CreatedAt, int Kind, string[][] Tags, string Content, string Sig);

internal sealed class NostrIdentity : IDisposable
{
    internal const int Kind = 24747;
    private readonly ECPrivKey key;
    public string PublicKey { get; }
    public NostrIdentity()
    {
        do { ECPrivKey.TryCreate(RandomNumberGenerator.GetBytes(32), out key!); } while (key == null);
        PublicKey = Convert.ToHexString(key.CreateXOnlyPubKey().ToBytes()).ToLowerInvariant();
    }

    public NostrEvent Sign(byte[] packet, string topic)
    {
        long time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string[][] tags = { new[] { "x", topic } };
        string content = Convert.ToBase64String(packet);
        byte[] hash = Hash(PublicKey, time, Kind, tags, content);
        var signature = key.SignBIP340(hash, (ReadOnlyMemory<byte>)RandomNumberGenerator.GetBytes(32));
        return new(Convert.ToHexString(hash).ToLowerInvariant(), PublicKey, time, Kind, tags, content, Convert.ToHexString(signature.ToBytes()).ToLowerInvariant());
    }

    private static byte[] Hash(string pubkey, long time, int kind, string[][] tags, string content)
        => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new object[] { 0, pubkey, time, kind, tags, content }, RoomKeys.Json));

    internal static bool Verify(NostrEvent e, string topic)
    {
        if (e.Kind != Kind || !RoomKeys.Hex(e.Pubkey, 64) || !RoomKeys.Hex(e.Id, 64) || !RoomKeys.Hex(e.Sig, 128)
            || e.Content == null || e.Content.Length > 1600 || e.Tags is not { Length: 1 } || e.Tags[0] is not { Length: 2 }
            || e.Tags[0][0] != "x" || e.Tags[0][1] != topic || Math.Abs((double)DateTimeOffset.UtcNow.ToUnixTimeSeconds() - e.CreatedAt) > 120) return false;
        byte[] hash = Hash(e.Pubkey, e.CreatedAt, e.Kind, e.Tags, e.Content);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(e.Id))
            && ECXOnlyPubKey.TryCreate(Convert.FromHexString(e.Pubkey), out var pub)
            && SecpSchnorrSignature.TryCreate(Convert.FromHexString(e.Sig), out var sig) && pub.SigVerifyBIP340(sig, hash);
    }

    public void Dispose() => key.Dispose();
}

internal static class Stun
{
    private const uint Cookie = 0x2112a442;
    internal static byte[] Request()
    {
        byte[] data = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(data, 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(4), Cookie);
        RandomNumberGenerator.Fill(data.AsSpan(8));
        return data;
    }

    internal static IPEndPoint? Read(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transaction)
    {
        if (data.Length < 20 || data.Length > 1024 || transaction.Length != 12 || BinaryPrimitives.ReadUInt16BigEndian(data) != 0x0101
            || BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != Cookie || !data.Slice(8, 12).SequenceEqual(transaction)
            || BinaryPrimitives.ReadUInt16BigEndian(data[2..]) != data.Length - 20) return null;
        for (int i = 20; i + 4 <= data.Length;)
        {
            int type = BinaryPrimitives.ReadUInt16BigEndian(data[i..]);
            int length = BinaryPrimitives.ReadUInt16BigEndian(data[(i + 2)..]);
            if (i + 4 + length > data.Length) return null;
            var value = data.Slice(i + 4, length);
            if (type == 0x0020 && length == 8 && value[0] == 0 && value[1] == 1)
            {
                int port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]) ^ (int)(Cookie >> 16);
                uint address = BinaryPrimitives.ReadUInt32BigEndian(value[4..]) ^ Cookie;
                byte[] bytes = new byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(bytes, address);
                return RoomKeys.Endpoint(new IPEndPoint(new IPAddress(bytes), port).ToString());
            }
            i += 4 + ((length + 3) & ~3);
        }
        return null;
    }
}
