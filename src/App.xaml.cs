using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ApexPing;

public partial class App : Application
{
    public UserSettings Settings { get; private set; } = new();
    public AssetCatalog Assets { get; private set; } = new(Path.Combine(AppContext.BaseDirectory, "assets", "native"));
    public NativeImages Images { get; private set; } = new();
    public bool Quitting { get; private set; }
    internal RoomSession? Room { get; private set; }
    private readonly List<OverlayWindow> overlays = new();
    private readonly List<MediaPlayer> players = new();
    private MediaPlayer? speech;
    private long nextSpeechAt;
    private string speakingLegend = "";
    private string? lastVoicePath;
    private string voiceSetting = "";
    private InputBridge? input;
    private OverlayWindow? wheelOwner;
    private SettingsWindow? settingsWindow;
    private Forms.NotifyIcon? tray;
    private System.Drawing.Icon? trayIcon;
    private Mutex? instance;
    private int anchorX, anchorY;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\ApexPing.Desktop", out bool first);
        if (!first) { MessageBox.Show("ApexPing 已在运行，请点击任务栏托盘图标打开设置。", "ApexPing"); Shutdown(); return; }
        Settings = SettingsStore.Load();
        voiceSetting = Settings.LegendId;
        LoadAssets();
        settingsWindow = new SettingsWindow(this);
        CreateTray();
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        try
        {
            CreateOverlays();
            input = new InputBridge(Dispatcher);
            input.Received += OnInput;
            input.Start();
            input.Configure(Settings);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { settingsWindow.SetStatus("启动失败：" + error.Message); }
        if (!e.Args.Contains("--tray")) settingsWindow.Show();
    }

    private void CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开设置", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add("好友房间", null, (_, _) => Dispatcher.Invoke(() => { ShowSettings(); settingsWindow?.FocusRoom(); }));
        menu.Items.Add("启用 / 暂停   Ctrl+Alt+P", null, (_, _) => Dispatcher.Invoke(Toggle));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出 ApexPing", null, (_, _) => Dispatcher.Invoke(Quit));
        using var iconStream = GetResourceStream(new Uri("pack://application:,,,/ApexPing;component/Assets/app.ico")).Stream;
        using var icon = new System.Drawing.Icon(iconStream, Forms.SystemInformation.SmallIconSize);
        trayIcon = (System.Drawing.Icon)icon.Clone();
        tray = new Forms.NotifyIcon { Text = "ApexPing · Alt + 左键拖动标记", Icon = trayIcon, ContextMenuStrip = menu, Visible = true };
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Dispatcher.Invoke(ShowSettings); };
    }

    private void ShowSettings() { settingsWindow?.Show(); if (settingsWindow != null) { settingsWindow.WindowState = WindowState.Normal; settingsWindow.Activate(); } }
    private void CreateOverlays()
    {
        CancelWheel();
        foreach (var window in overlays) window.Close();
        overlays.Clear();
        foreach (var screen in Forms.Screen.AllScreens) { var window = new OverlayWindow(screen, Assets, Images); overlays.Add(window); window.Show(); }
    }

    private void DisplayChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(CreateOverlays);
    private OverlayWindow WindowAt(int x, int y)
    {
        string name = Forms.Screen.FromPoint(new System.Drawing.Point(x, y)).DeviceName;
        var screens = Forms.Screen.AllScreens;
        int index = Array.FindIndex(screens, screen => screen.DeviceName == name);
        return overlays[index >= 0 && index < overlays.Count ? index : 0];
    }

    private void OnInput(InputEvent e)
    {
        switch (e.Type)
        {
            case "ready": settingsWindow?.SetStatus(SettingsStore.LoadError ?? "就绪 · 按住 Alt + 左键拖动，在桌面放置标记。"); break;
            case "error": CancelWheel(); settingsWindow?.SetStatus(e.Message ?? "输入组件错误。 "); break;
            case "toggled": Settings.Enabled = e.Enabled; SaveSettings(); settingsWindow?.RefreshState(); break;
            case "wheelOpen":
                CancelWheel(); anchorX = e.X; anchorY = e.Y; wheelOwner = WindowAt(e.X, e.Y);
                wheelOwner.OpenWheel(e.X, e.Y, Settings.Scale);
                PlaySound(Assets.EventPathFor("wheelOpen")); break;
            case "wheelMove": wheelOwner?.MoveWheel(e.X, e.Y, Settings.Scale); break;
            case "wheelRelease":
                int selected = wheelOwner?.ReleaseWheel(e.X, e.Y, Settings.Scale) ?? -1;
                wheelOwner = null;
                if (selected >= 0) PlacePing(selected, anchorX, anchorY);
                break;
            case "cancel": CancelWheel(); break;
        }
    }

    private void CancelWheel() { wheelOwner?.CancelWheel(); wheelOwner = null; }
    private void PlacePing(int index, int x, int y, bool share = true)
    {
        WindowAt(x, y).Ping(index, x, y, Settings);
        PlaySound(index);
        PlayVoice(index);
        if (share && Room != null)
        {
            var screens = OrderedScreens();
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(x, y));
            int display = Array.FindIndex(screens, item => item.DeviceName == screen.DeviceName) + 1;
            var bounds = screen.Bounds;
            Room.SendPing(Pings.All[index].Id, display,
                (double)(x - bounds.X) / Math.Max(1, bounds.Width - 1), (double)(y - bounds.Y) / Math.Max(1, bounds.Height - 1));
        }
        if (Images.Error != null) settingsWindow?.SetStatus(Images.Error);
    }

    public void Preview(int index)
    {
        if (overlays.Count == 0) { PlaySound(index); PlayVoice(index); return; }
        var bounds = Forms.Screen.PrimaryScreen!.WorkingArea;
        PlacePing(index, bounds.X + bounds.Width / 2, bounds.Y + 90, false);
    }

    private static Forms.Screen[] OrderedScreens() => Forms.Screen.AllScreens.OrderByDescending(s => s.Primary).ThenBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ToArray();

    internal void JoinRoom(string code)
    {
        if (Room != null) return;
        var room = new RoomSession(code, Settings.PlayerName, Settings.LegendId, Settings.Enabled, Settings.AllowInternet);
        Room = room;
        room.Changed += state =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (Room == room) settingsWindow?.RefreshRoom(state); });
        };
        room.Received += ping =>
        {
            if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => { if (Room == room) ReceivePing(ping); });
        };
        room.Start(); settingsWindow?.RefreshRoom();
    }

    internal async Task LeaveRoom()
    {
        var room = Room; Room = null; settingsWindow?.RefreshRoom();
        if (room != null) await room.DisposeAsync();
    }

    private void ReceivePing(SharedPing ping)
    {
        if (!Settings.Enabled || Settings.RoomMuted || overlays.Count == 0 || Quitting) return;
        var screens = OrderedScreens();
        var screen = screens[ping.Display <= screens.Length ? ping.Display - 1 : 0];
        var bounds = screen.Bounds;
        int x = bounds.X + (int)Math.Round(ping.X * (bounds.Width - 1)), y = bounds.Y + (int)Math.Round(ping.Y * (bounds.Height - 1));
        var window = WindowAt(x, y);
        if (Settings.PauseFullscreen && window.CoveredByFullscreenApp()) return;
        int index = Array.FindIndex(Pings.All, p => p.Id == ping.Ping);
        if (index < 0) return;
        window.Ping(index, x, y, Settings, ping.Name); PlaySound(index); PlayVoice(index, ping.Legend);
        if (Images.Error != null) settingsWindow?.SetStatus(Images.Error);
    }

    private void PlaySound(int index)
    {
        PlaySound(Assets.PathFor(Pings.All[index].Id, false));
    }

    private void PlaySound(string? path)
    {
        if (Settings.Muted || Settings.Volume <= 0 || path == null) return;
        // Retain event tails when another ping is placed; cap spam and leave mix headroom.
        if (players.Count >= 4) { var oldest = players[0]; players.RemoveAt(0); oldest.Close(); }
        var player = new MediaPlayer();
        void Close() { if (players.Remove(player)) { player.Close(); UpdateSoundVolumes(); } }
        player.MediaOpened += (_, _) => { if (players.Contains(player)) player.Play(); };
        player.MediaEnded += (_, _) => Close();
        player.MediaFailed += (_, error) => { Close(); settingsWindow?.SetStatus("原生事件音效播放失败：" + error.ErrorException.Message); };
        players.Add(player);
        UpdateSoundVolumes();
        try { player.Open(new Uri(path)); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { Close(); settingsWindow?.SetStatus("音效播放失败：" + e.Message); }
    }

    private void UpdateSoundVolumes()
    {
        double volume = Settings.Muted ? 0 : Settings.Volume * (speech == null ? 0.5 : 0.25) / Math.Max(1, players.Count);
        foreach (var player in players) player.Volume = volume;
        if (speech != null) speech.Volume = Settings.Muted ? 0 : Settings.Volume * 0.25 * Math.Pow(10, Assets.Manifest.Legends[speakingLegend].GainDb / 20);
    }

    private void PlayVoice(int index, string? legend = null)
    {
        if (Settings.Muted || Settings.LegendId == "" || Settings.Volume <= 0 || speech != null || Environment.TickCount64 < nextSpeechAt) return;
        string selectedLegend = legend ?? Settings.LegendId;
        var paths = Assets.VoicePathsFor(selectedLegend, Pings.All[index].Id);
        if (paths.Length == 0) return;
        var choices = paths.Length > 1 ? paths.Where(path => path != lastVoicePath).ToArray() : paths;
        string path = choices[Random.Shared.Next(choices.Length)];
        var player = new MediaPlayer();
        speech = player; speakingLegend = selectedLegend; lastVoicePath = path;
        int delay = Assets.Manifest.Legends[speakingLegend].DelayMs;
        UpdateSoundVolumes();
        player.MediaOpened += async (_, _) =>
        {
            await System.Threading.Tasks.Task.Delay(delay);
            if (ReferenceEquals(speech, player)) player.Play();
        };
        player.MediaEnded += (_, _) => { if (ReferenceEquals(speech, player)) { StopSpeech(); nextSpeechAt = Environment.TickCount64 + 250; } };
        player.MediaFailed += (_, error) =>
        {
            if (!ReferenceEquals(speech, player)) return;
            StopSpeech(); settingsWindow?.SetStatus("角色语音播放失败：" + error.ErrorException.Message);
        };
        try { player.Open(new Uri(path)); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { StopSpeech(); settingsWindow?.SetStatus("角色语音播放失败：" + error.Message); }
    }

    private void StopSpeech()
    {
        var player = speech; speech = null; player?.Close(); UpdateSoundVolumes();
    }

    public void Toggle() { Settings.Enabled = !Settings.Enabled; if (!Settings.Enabled) CancelWheel(); SaveSettings(); settingsWindow?.RefreshState(); }
    public void SaveSettings()
    {
        Settings.Normalize();
        if (voiceSetting != Settings.LegendId) { StopSpeech(); nextSpeechAt = 0; voiceSetting = Settings.LegendId; lastVoicePath = null; }
        UpdateSoundVolumes();
        CancelWheel(); input?.Configure(Settings);
        Room?.UpdateProfile(Settings.PlayerName, Settings.LegendId, Settings.Enabled);
        try { SettingsStore.Save(Settings); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { settingsWindow?.SetStatus("设置未能保存：" + e.Message); }
        if (tray != null) tray.Text = Settings.Enabled ? "ApexPing · 已启用" : "ApexPing · 已暂停";
    }

    private void LoadAssets()
    {
        string folder = Settings.AssetFolder ?? Path.Combine(AppContext.BaseDirectory, "assets", "native");
        try { Assets = new AssetCatalog(folder); }
        catch (ArgumentException) { Settings.AssetFolder = null; Assets = new(Path.Combine(AppContext.BaseDirectory, "assets", "native")); }
        Images = new();
    }

    public void ReloadAssets() { StopSpeech(); nextSpeechAt = 0; LoadAssets(); CreateOverlays(); settingsWindow?.RefreshAssets(); }
    private async void Quit() { if (Quitting) return; Quitting = true; await LeaveRoom(); Shutdown(); }

    protected override void OnExit(ExitEventArgs e)
    {
        Quitting = true; SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        Room?.Dispose(); Room = null;
        input?.Dispose(); StopSpeech(); foreach (var player in players.ToArray()) player.Close(); players.Clear(); tray?.Dispose(); trayIcon?.Dispose();
        foreach (var window in overlays) window.Close();
        instance?.Dispose(); base.OnExit(e);
    }
}
