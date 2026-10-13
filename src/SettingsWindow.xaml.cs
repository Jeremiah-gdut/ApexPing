using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace ApexPing;

public partial class SettingsWindow : Window
{
    private readonly App owner;
    private bool initialized;
    private WheelView? wheel;

    public SettingsWindow(App owner)
    {
        this.owner = owner;
        InitializeComponent();
        var settings = owner.Settings;
        foreach (ComboBoxItem item in TriggerBox.Items) if ((string)item.Tag == settings.Trigger) TriggerBox.SelectedItem = item;
        FullscreenBox.IsChecked = settings.PauseFullscreen;
        MuteBox.IsChecked = settings.Muted;
        VolumeSlider.Value = settings.Volume * 100;
        ScaleSlider.Value = settings.Scale * 100;
        DurationSlider.Value = settings.Duration;
        for (int i = 0; i < Pings.All.Length; i++)
        {
            int index = i;
            var button = new Button { Content = Pings.All[i].Label, FontSize = 10, Padding = new Thickness(4, 8, 4, 8), Margin = new Thickness(3) };
            button.Click += (_, _) => { wheel?.Select(index); owner.Preview(index); };
            PingButtons.Children.Add(button);
        }
        initialized = true;
        RefreshAssets(); RefreshState(); RefreshLabels();
    }

    public void RefreshAssets()
    {
        bool wasInitialized = initialized; initialized = false;
        wheel = new WheelView(owner.Assets, owner.Images) { Width = 342, Height = 342, Preview = true };
        WheelHost.Content = wheel;
        var assets = owner.Assets;
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
        ToggleButton.Content = owner.Settings.Enabled ? "● 已启用" : "○ 已暂停";
        ToggleButton.Background = (Brush)new BrushConverter().ConvertFromString(owner.Settings.Enabled ? "#315846" : "#40373A")!;
    }

    public void SetStatus(string message) => StatusText.Text = message;

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
        RefreshLabels(); owner.SaveSettings();
    }

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
