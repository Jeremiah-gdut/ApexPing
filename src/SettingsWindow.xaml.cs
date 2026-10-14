using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ApexPing;

public partial class SettingsWindow : Window
{
    private readonly App owner;
    private bool initialized;
    private WheelView? wheel;
    private int previewIndex;

    public SettingsWindow(App owner)
    {
        this.owner = owner;
        WindowTheme.Apply(this);
        InitializeComponent();
        var work = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, work.Width - 32); MinHeight = Math.Min(MinHeight, work.Height - 32);
        Width = Math.Min(Width, work.Width - 32); Height = Math.Min(Height, work.Height - 32);
        SourceInitialized += (_, _) => WindowTheme.Apply(this);
        SystemEvents.UserPreferenceChanged += ThemeChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= ThemeChanged;
        AppVersionText.Text = "v" + (typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.4.0");
        var settings = owner.Settings;
        foreach (ComboBoxItem item in TriggerBox.Items) if ((string)item.Tag == settings.Trigger) TriggerBox.SelectedItem = item;
        FullscreenBox.IsChecked = settings.PauseFullscreen;
        MuteBox.IsChecked = settings.Muted;
        VolumeSlider.Value = settings.Volume * 100;
        ScaleSlider.Value = settings.Scale * 100;
        DurationSlider.Value = settings.Duration;
        PlayerNameBox.Text = settings.PlayerName;
        RoomInternetBox.IsChecked = settings.AllowInternet;
        RoomMutedBox.IsChecked = settings.RoomMuted;
        for (int i = 0; i < Pings.All.Length; i++)
        {
            int index = i;
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Image { Width = 18, Height = 18, Margin = new Thickness(0, 0, 8, 0) });
            content.Children.Add(new TextBlock { Text = Pings.All[i].Label, VerticalAlignment = VerticalAlignment.Center });
            var button = new Button { Content = content, FontSize = 12, Padding = new Thickness(6, 11, 6, 11), Margin = new Thickness(3), ToolTip = Pings.All[i].English };
            System.Windows.Automation.AutomationProperties.SetName(button, "预览" + Pings.All[i].Label);
            button.Click += (_, _) => { previewIndex = index; SelectPreview(index); owner.Preview(index); };
            PingButtons.Children.Add(button);
        }
        initialized = true;
        RefreshAssets(); RefreshState(); RefreshLabels(); RefreshRoom(); Navigate("wheel");
    }

    public void RefreshAssets()
    {
        bool wasInitialized = initialized; initialized = false;
        wheel = new WheelView(owner.Assets, owner.Images) { Width = 340, Height = 340, Preview = true };
        WheelHost.Content = wheel;
        var assets = owner.Assets;
        for (int i = 0; i < Pings.All.Length; i++)
            ((Image)((StackPanel)((Button)PingButtons.Children[i]).Content).Children[0]).Source = owner.Images.GetColored(assets.PathFor(Pings.All[i].Id, true), (Color)ColorConverter.ConvertFromString(assets.ColorFor(Pings.All[i].Id)));
        SelectPreview(previewIndex);
        AssetStatus.Text = assets.Error ?? $"原生图标 {assets.IconCount} / 8 · 标记音效 {assets.SoundFileCount} 类" +
            (assets.EventPathFor("wheelOpen") != null ? " · 轮盘打开音效已载入" : "");
        AssetPath.Text = assets.Root; AssetPath.ToolTip = assets.Root;
        LegendBox.Items.Clear();
        LegendBox.Items.Add(new ComboBoxItem { Content = "关闭角色语音", Tag = "" });
        foreach (var entry in assets.Manifest.Legends.OrderBy(entry => entry.Value.Name))
        {
            if (Pings.All.Any(ping => assets.VoicePathsFor(entry.Key, ping.Id).Length == 0)) continue;
            LegendBox.Items.Add(new ComboBoxItem { Content = entry.Value.Name, Tag = entry.Key });
        }
        LegendBox.SelectedIndex = 0;
        foreach (ComboBoxItem item in LegendBox.Items) if ((string)item.Tag == owner.Settings.LegendId) LegendBox.SelectedItem = item;
        LegendBox.IsEnabled = LegendBox.Items.Count > 1;
        initialized = wasInitialized;
    }

    public void RefreshState()
    {
        ToggleButton.Content = owner.Settings.Enabled ? "暂停标记" : "启用标记";
        EnabledText.Text = owner.Settings.Enabled ? "已启用" : "已暂停";
        EnabledDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, owner.Settings.Enabled ? "SuccessBrush" : "MutedBrush");
    }

    public void SetStatus(string message) => StatusText.Text = message;

    internal void RefreshRoom(RoomState? state = null)
    {
        bool active = owner.Room != null;
        RoomCreateButton.IsEnabled = RoomJoinButton.IsEnabled = RoomInternetBox.IsEnabled = !active;
        RoomCopyButton.IsEnabled = RoomLeaveButton.IsEnabled = active;
        RoomCodeBox.IsReadOnly = active;
        if (active) RoomCodeBox.Text = owner.Room!.Code;
        RoomStatus.Text = state == null ? active ? "正在加入房间…" : "尚未加入房间。创建后将房间码发给朋友。"
            : $"{state.Members.Length}/8 人 · {state.Status}";
        RoomMembers.ItemsSource = state?.Members;
    }

    private void SettingChanged(object sender, RoutedEventArgs e)
    {
        if (!initialized) return;
        var settings = owner.Settings;
        settings.Trigger = (string)((ComboBoxItem)TriggerBox.SelectedItem).Tag;
        settings.PauseFullscreen = FullscreenBox.IsChecked == true;
        settings.Muted = MuteBox.IsChecked == true;
        settings.Volume = VolumeSlider.Value / 100;
        settings.Scale = ScaleSlider.Value / 100;
        settings.Duration = DurationSlider.Value;
        settings.LegendId = (string)((ComboBoxItem)LegendBox.SelectedItem).Tag;
        settings.PlayerName = PlayerNameBox.Text;
        settings.AllowInternet = RoomInternetBox.IsChecked == true;
        settings.RoomMuted = RoomMutedBox.IsChecked == true;
        RefreshLabels(); owner.SaveSettings();
        PlayerNameBox.Text = settings.PlayerName;
    }

    private void RoomCreate_Click(object sender, RoutedEventArgs e) => EnterRoom(RoomKeys.CreateCode());
    internal void FocusRoom()
    {
        NavRoom.IsChecked = true;
        Navigate("room");
        Dispatcher.BeginInvoke(() => RoomCodeBox.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void ThemeChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(() => WindowTheme.Apply(this));
    }

    private void Navigation_Checked(object sender, RoutedEventArgs e)
    {
        if (initialized && sender is RadioButton { Tag: string page }) Navigate(page);
    }

    private void Navigate(string page)
    {
        foreach (var panel in new[] { WheelPage, SoundPage, InputPage, RoomPage, AssetsPage }) panel.Visibility = Visibility.Collapsed;
        var (panelToShow, title, description) = page switch
        {
            "sound" => (SoundPage, "声音与角色", "让每一次标记，都带上你的声音。"),
            "input" => (InputPage, "快捷键与交互", "用顺手的方式，在桌面上发出信号。"),
            "room" => (RoomPage, "好友房间", "分享房间码，和朋友一起标记。"),
            "assets" => (AssetsPage, "资源与应用", "管理原生资源和后台运行。"),
            _ => (WheelPage, "轮盘与标记", "选择一个标记，看看它在桌面上的样子。")
        };
        panelToShow.Visibility = Visibility.Visible;
        PageTitle.Text = title; PageDescription.Text = description;
        SettingsScroll.ScrollToTop();
    }

    private void SelectPreview(int index)
    {
        wheel?.Select(index);
        PreviewTitle.Text = index >= 0 ? Pings.All[index].Label : "取消标记";
        PreviewEnglish.Text = index >= 0 ? Pings.All[index].English : "CANCEL";
        PreviewEnglish.Foreground = index >= 0 ? new SolidColorBrush((Color)ColorConverter.ConvertFromString(owner.Assets.ColorFor(Pings.All[index].Id))) : Brushes.LightGray;
        for (int i = 0; i < PingButtons.Children.Count; i++)
        {
            var button = (Button)PingButtons.Children[i];
            button.SetResourceReference(BackgroundProperty, i == index ? "SelectedBrush" : "SurfaceBrush");
            button.SetResourceReference(BorderBrushProperty, i == index ? "AccentBrush" : "StrokeBrush");
        }
    }

    private void WheelPreview_Move(object sender, MouseEventArgs e)
    {
        if (wheel == null || WheelHost.ActualWidth <= 0) return;
        var point = e.GetPosition(WheelHost);
        double factor = 380 / WheelHost.ActualWidth;
        SelectPreview(Pings.Pick((point.X - WheelHost.ActualWidth / 2) * factor, (point.Y - WheelHost.ActualHeight / 2) * factor, 70));
    }
    private void WheelPreview_Leave(object sender, MouseEventArgs e) => SelectPreview(previewIndex);
    private void WheelPreview_Click(object sender, MouseButtonEventArgs e)
    {
        if (wheel?.Selected is >= 0 and var index) { previewIndex = index; owner.Preview(index); }
    }
    private void RoomJoin_Click(object sender, RoutedEventArgs e) => EnterRoom(RoomCodeBox.Text);
    private void EnterRoom(string code)
    {
        try
        {
            owner.Settings.PlayerName = PlayerNameBox.Text;
            owner.SaveSettings(); owner.JoinRoom(code);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.Net.Sockets.SocketException)
        { RoomStatus.Text = error.Message; }
    }

    private void RoomCopy_Click(object sender, RoutedEventArgs e)
    {
        if (owner.Room == null) return;
        try { Clipboard.SetText(owner.Room.Code); RoomStatus.Text = "房间码已复制，将它发给朋友即可。"; }
        catch (System.Runtime.InteropServices.ExternalException) { RoomStatus.Text = "剪贴板暂时被占用，请稍后重试，或直接选择并复制房间码。"; }
    }
    private async void RoomLeave_Click(object sender, RoutedEventArgs e) { RoomLeaveButton.IsEnabled = false; await owner.LeaveRoom(); }

    private void RefreshLabels()
    {
        VolumeLabel.Text = $"{VolumeSlider.Value:0}%";
        ScaleLabel.Text = $"{ScaleSlider.Value:0}%";
        DurationLabel.Text = $"{DurationSlider.Value:0.0} 秒";
        GestureHelp.Text = owner.Settings.Trigger.StartsWith("mouse")
            ? "按住侧键拖动，松开标记；中央 / 右键 / Esc 取消。"
            : "按住触发键 + 左键拖动，松开左键标记；中央 / 右键 / Esc 取消。";
    }

    private void Toggle_Click(object sender, RoutedEventArgs e) => owner.Toggle();
    private void Reload_Click(object sender, RoutedEventArgs e) => owner.ReloadAssets();
    private void Hide_Click(object sender, RoutedEventArgs e) => Hide();
    private void Assets_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog { Description = "选择包含 manifest.json 的 Apex 原生资源文件夹", UseDescriptionForTitle = true };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        var catalog = new AssetCatalog(dialog.SelectedPath);
        if (catalog.Error != null) { SetStatus(catalog.Error); return; }
        owner.Settings.AssetFolder = dialog.SelectedPath;
        owner.SaveSettings(); owner.ReloadAssets();
    }

    protected override void OnClosing(CancelEventArgs e) { e.Cancel = !owner.Quitting; if (e.Cancel) Hide(); base.OnClosing(e); }
}
