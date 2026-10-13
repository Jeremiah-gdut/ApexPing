using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ApexPing;

public sealed record PingDef(string Id, string Label, string English, string Color);

public static class Pings
{
    // Apex's default wheel, starting at the top and proceeding clockwise.
    public static readonly PingDef[] All =
    {
        new("go", "前往这里", "GO", "#FFB400"),
        new("enemy", "发现敌人", "ENEMY", "#FF310D"),
        new("loot", "搜寻物资", "LOOTING", "#BFFF14"),
        new("attack", "进攻这里", "ATTACKING", "#FF310D"),
        new("going", "我去这里", "GOING", "#FFB400"),
        new("defend", "防守这里", "DEFENDING", "#00FFF6"),
        new("watch", "留意这里", "WATCHING", "#FF310D"),
        new("been", "有人来过", "BEEN HERE", "#FF310D"),
    };

    public static int Pick(double dx, double dy, double deadZone = 44)
    {
        if (dx * dx + dy * dy < deadZone * deadZone) return -1;
        double degrees = (Math.Atan2(dy, dx) * 180 / Math.PI + 472.5) % 360;
        return (int)(degrees / 45) % All.Length;
    }

    public static (double X, double Y) Position(int index, double radius)
    {
        double angle = (index * 45 - 90) * Math.PI / 180;
        return (Math.Cos(angle) * radius, Math.Sin(angle) * radius);
    }
}

public sealed record NativeAsset(string? Icon, string? Sound, string? Color = null,
    string? InnerIcon = null, string? InnerShadowIcon = null, string? ShadowIcon = null, string? AnimationIcon = null);
public sealed record LegendVoice(string Name, Dictionary<string, string[]> Pings, int DelayMs = 200, double GainDb = 5);

public sealed class AssetManifest
{
    public int FormatVersion { get; set; }
    public string SourceGame { get; set; } = "";
    public string SteamBuildId { get; set; } = "";
    public string Extractor { get; set; } = "";
    public Dictionary<string, NativeAsset> Pings { get; set; } = new();
    public Dictionary<string, string> Events { get; set; } = new();
    public Dictionary<string, LegendVoice> Legends { get; set; } = new();
    public Dictionary<string, string> Wheel { get; set; } = new();
}

public sealed class AssetCatalog
{
    public static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, WriteIndented = true };
    public string Root { get; }
    public AssetManifest Manifest { get; }
    public string? Error { get; }
    public int IconCount => Pings.All.Count(p => PathFor(p.Id, true) != null);
    public int SoundCount => Pings.All.Count(p => PathFor(p.Id, false) != null);
    public int SoundFileCount => Pings.All.Select(p => PathFor(p.Id, false)).Where(path => path != null).Distinct(StringComparer.OrdinalIgnoreCase).Count();

    public AssetCatalog(string root)
    {
        Root = Path.GetFullPath(root);
        Manifest = new();
        try
        {
            string path = Path.Combine(Root, "manifest.json");
            if (!File.Exists(path)) { Error = "未找到原生资源清单，请先提取资源或选择资源文件夹。"; return; }
            if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("资源清单过大。 ");
            Manifest = JsonSerializer.Deserialize<AssetManifest>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("资源清单为空。 ");
            if (Manifest.FormatVersion != 1 || Manifest.SourceGame != "Apex Legends" || Manifest.Pings == null || Manifest.Events == null || Manifest.Legends == null || Manifest.Wheel == null)
                throw new InvalidDataException("不支持的 Apex 资源清单。 ");
            foreach (var asset in Manifest.Pings.Values)
            {
                if (asset == null) throw new InvalidDataException("资源条目为空。 ");
                Resolve(asset.Icon, ".png");
                Resolve(asset.Sound, ".wav");
                Resolve(asset.InnerIcon, ".png"); Resolve(asset.InnerShadowIcon, ".png");
                Resolve(asset.ShadowIcon, ".png"); Resolve(asset.AnimationIcon, ".png");
                if (asset.Color != null && !Regex.IsMatch(asset.Color, "^#[0-9A-Fa-f]{6}$"))
                    throw new InvalidDataException("原生标记颜色必须是 RGB 十六进制值。 ");
            }
            foreach (string sound in Manifest.Events.Values) Resolve(sound, ".wav");
            foreach (string image in Manifest.Wheel.Values) Resolve(image, ".png");
            if (Manifest.Legends.Count > 64) throw new InvalidDataException("角色清单过大。 ");
            foreach (var legend in Manifest.Legends.Values)
            {
                if (legend == null || string.IsNullOrWhiteSpace(legend.Name) || legend.Name.Length > 80 || legend.Pings == null
                    || legend.DelayMs < 0 || legend.DelayMs > 2000 || !double.IsFinite(legend.GainDb) || legend.GainDb < -60 || legend.GainDb > 6)
                    throw new InvalidDataException("角色语音清单无效。 ");
                foreach (var lines in legend.Pings.Values)
                {
                    if (lines == null || lines.Length == 0 || lines.Length > 32) throw new InvalidDataException("角色语音变体数量无效。 ");
                    foreach (string line in lines)
                        if (string.IsNullOrWhiteSpace(line)) throw new InvalidDataException("角色语音路径为空。 ");
                        else Resolve(line, ".wav");
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            Manifest = new();
            Error = e.Message;
        }
    }

    public string? PathFor(string id, bool icon)
    {
        if (Error != null || !Manifest.Pings.TryGetValue(id, out var asset)) return null;
        return Resolve(icon ? asset.Icon : asset.Sound, icon ? ".png" : ".wav");
    }

    public string ColorFor(string id) => Error == null && Manifest.Pings.TryGetValue(id, out var asset) && asset.Color != null
        ? asset.Color : Pings.All.First(p => p.Id == id).Color;

    public string? LayerPathFor(string id, string layer)
    {
        if (Error != null || !Manifest.Pings.TryGetValue(id, out var asset)) return null;
        return Resolve(layer switch
        {
            "inner" => asset.InnerIcon, "innerShadow" => asset.InnerShadowIcon,
            "shadow" => asset.ShadowIcon, "animation" => asset.AnimationIcon,
            _ => throw new ArgumentException("Unknown native ping layer", nameof(layer))
        }, ".png");
    }

    public string? EventPathFor(string id) => Error == null && Manifest.Events.TryGetValue(id, out var sound) ? Resolve(sound, ".wav") : null;
    public string? WheelPathFor(string id) => Error == null && Manifest.Wheel.TryGetValue(id, out var path) ? Resolve(path, ".png") : null;
    public string[] VoicePathsFor(string legend, string ping) => Error == null && Manifest.Legends.TryGetValue(legend, out var voice) && voice.Pings.TryGetValue(ping, out var lines)
        ? lines.Select(line => Resolve(line, ".wav")).Where(path => path != null).Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : Array.Empty<string>();

    private string? Resolve(string? relative, string extension)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string full = Path.GetFullPath(Path.Combine(Root, relative));
        if (Path.IsPathRooted(relative) || !full.StartsWith(Root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(full).Equals(extension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("资源路径必须是文件夹内的 PNG / WAV 文件。 ");
        if (!File.Exists(full)) return null;
        long size = new FileInfo(full).Length;
        if (size == 0 || size > 16 * 1024 * 1024) throw new InvalidDataException("资源文件大小无效。 ");
        return full;
    }
}

public readonly record struct PingFrame(double BurstScale, double BurstOpacity, double PulseScale, double PulseOpacity,
    double SecondPulseScale, double SecondPulseOpacity);

public static class PingAnimation
{
    // waypoint_ping_entpos, build 25682878: sampled from the native UI function at 60 Hz.
    public static PingFrame At(double seconds)
    {
        double t = Math.Clamp(seconds * 2, 0, 1);
        double enter = t * t * (3 - 2 * t);
        double pulse = seconds - Math.Floor(seconds), second = (seconds + 0.5) % 1;
        // The native two-pin bounds expand from [-0.1, 1.1] to [-3, 4].
        return new(1.2 + 5.8 * enter, 1 - enter, 1 + pulse, 1 - pulse, 1 + second, 1 - second);
    }

    public static double Fade(double age, double duration)
    {
        double t = Math.Clamp((age - duration + 0.5) * 2, 0, 1);
        return (1 - t) * (1 - t);
    }
}

public static class WheelAnimation
{
    // Native comms_menu fast entrance and finished branch, build 25682878.
    public static double Open(double age) => Smooth(Math.Clamp(age * 10, 0, 1));
    public static double Close(double age) => 1 - Smooth(Math.Clamp(age / 0.525, 0, 1));
    public static double Follow(double current, double target, double elapsed)
        => target + (current - target) * Math.Exp(-Math.Max(0, elapsed) / 0.04);
    private static double Smooth(double t) => t * t * (3 - 2 * t);
}

public sealed class UserSettings
{
    public string Trigger { get; set; } = "alt";
    public bool Enabled { get; set; } = true;
    public bool PauseFullscreen { get; set; } = true;
    public bool Muted { get; set; }
    public double Volume { get; set; } = 0.55;
    public double Scale { get; set; } = 1;
    public double Duration { get; set; } = 1.5;
    public string? AssetFolder { get; set; }
    public string LegendId { get; set; } = "wraith";

    public void Normalize()
    {
        if (!new[] { "alt", "ctrl", "shift", "mouse4", "mouse5" }.Contains(Trigger)) Trigger = "alt";
        if (LegendId == null || !Regex.IsMatch(LegendId, "^[a-z0-9_]{0,40}$")) LegendId = "wraith";
        Volume = double.IsFinite(Volume) ? Math.Clamp(Volume, 0, 1) : 0.55;
        Scale = double.IsFinite(Scale) ? Math.Clamp(Scale, 0.7, 1.5) : 1;
        Duration = double.IsFinite(Duration) ? Math.Clamp(Duration, 0.6, 3) : 1.5;
    }
}

public static class SettingsStore
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ApexPing");
    public static string SettingsPath => Path.Combine(Folder, "settings.json");
    public static string? LoadError { get; private set; }

    public static UserSettings Load()
    {
        try
        {
            var settings = File.Exists(SettingsPath)
                ? JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(SettingsPath), AssetCatalog.JsonOptions) ?? new() : new();
            settings.Normalize();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            LoadError = "设置读取失败，已使用默认值：" + e.Message;
            return new();
        }
    }

    public static void Save(UserSettings settings)
    {
        settings.Normalize();
        Directory.CreateDirectory(Folder);
        string temp = SettingsPath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, AssetCatalog.JsonOptions));
        File.Move(temp, SettingsPath, true);
    }
}
