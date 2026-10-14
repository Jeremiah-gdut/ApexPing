using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ApexPing;

public sealed class OverlayWindow : Window
{
    private readonly Canvas canvas = new() { Background = Brushes.Transparent, IsHitTestVisible = false };
    private readonly Forms.Screen screen;
    private readonly AssetCatalog assets;
    private readonly NativeImages images;
    private WheelView? wheel;
    private Point anchor;
    private readonly List<(Grid Element, PingView View, TextBlock Label, double Start, double Duration)> markers = new();
    private readonly List<(WheelView View, double Start)> closingWheels = new();
    private readonly Stopwatch animationClock = Stopwatch.StartNew();
    private double wheelStart;
    private bool rendering;
    private TimeSpan lastFrame = TimeSpan.MinValue;

    public OverlayWindow(Forms.Screen screen, AssetCatalog assets, NativeImages images)
    {
        this.screen = screen; this.assets = assets; this.images = images;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
        Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
        Content = canvas; IsHitTestVisible = false; Focusable = false;
        SourceInitialized += (_, _) =>
        {
            nint handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, GetWindowLongPtr(handle, -20) | 0x08000000 | 0x20 | 0x80);
            PositionOverlay();
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
        };
        // WPF applies a DPI resize when moving to a different monitor; position after that layout has settled.
        Loaded += (_, _) => Dispatcher.BeginInvoke(PositionOverlay, DispatcherPriority.ContextIdle);
        Closed += (_, _) => { StopRendering(); markers.Clear(); closingWheels.Clear(); };
    }

    private void PositionOverlay()
    {
        var bounds = screen.Bounds;
        SetWindowPos(new WindowInteropHelper(this).Handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x10);
    }

    private static nint WindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == 0x84) { handled = true; return new nint(-1); }
        if (msg == 0x21) { handled = true; return new nint(3); }
        return nint.Zero;
    }

    public void OpenWheel(int x, int y, double scale)
    {
        if (wheel != null) canvas.Children.Remove(wheel);
        foreach (var closing in closingWheels) canvas.Children.Remove(closing.View);
        closingWheels.Clear();
        var pointer = PointFromScreen(new Point(x, y));
        double radius = 190 * scale;
        anchor = new Point(ClampCentre(pointer.X, ActualWidth, radius), ClampCentre(pointer.Y, ActualHeight, radius));
        wheel = new WheelView(assets, images) { Width = 380 * scale, Height = 380 * scale, Opacity = 0 };
        wheelStart = animationClock.Elapsed.TotalSeconds;
        Canvas.SetLeft(wheel, anchor.X - wheel.Width / 2);
        Canvas.SetTop(wheel, anchor.Y - wheel.Height / 2);
        canvas.Children.Add(wheel);
        StartRendering();
    }

    private static double ClampCentre(double value, double extent, double radius)
        => extent < radius * 2 ? extent / 2 : Math.Clamp(value, radius, extent - radius);

    public void MoveWheel(int x, int y, double scale)
    {
        if (wheel == null) return;
        var point = PointFromScreen(new Point(x, y));
        wheel.Select(Pings.Pick(point.X - anchor.X, point.Y - anchor.Y, 70 * scale));
    }

    public int ReleaseWheel(int x, int y, double scale)
    {
        MoveWheel(x, y, scale);
        int selected = wheel?.Selected ?? -1;
        CancelWheel();
        return selected;
    }

    public void CancelWheel()
    {
        if (wheel != null)
        {
            wheel.BeginClose();
            closingWheels.Add((wheel, wheelStart));
            StartRendering();
        }
        wheel = null;
    }

    public void Ping(int index, int x, int y, UserSettings settings, string? sender = null)
    {
        var point = PointFromScreen(new Point(x, y));
        var ping = Pings.All[index];
        var color = (Color)ColorConverter.ConvertFromString(assets.ColorFor(ping.Id));
        var marker = new Grid { Width = 150, Height = 150, IsHitTestVisible = false };
        marker.RenderTransformOrigin = new Point(0.5, 0.5);
        marker.RenderTransform = new ScaleTransform(settings.Scale, settings.Scale);
        var view = new PingView(index, assets, images);
        marker.Children.Add(view);
        var label = new TextBlock
        {
            Text = sender == null ? ping.Label : ping.Label + " · " + sender, Foreground = new SolidColorBrush(color), FontSize = 12,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 150,
            FontFamily = new FontFamily("Microsoft YaHei UI"), Background = new SolidColorBrush(Color.FromArgb(220, 12, 15, 20)),
            Padding = new Thickness(8, 3, 8, 3), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3)
        };
        marker.Children.Add(label);
        Canvas.SetLeft(marker, point.X - 75); Canvas.SetTop(marker, point.Y - 75);
        marker.Opacity = 0;
        canvas.Children.Add(marker);
        markers.Add((marker, view, label, animationClock.Elapsed.TotalSeconds, settings.Duration));
        if (markers.Count > 12) { canvas.Children.Remove(markers[0].Element); markers.RemoveAt(0); }
        StartRendering();
    }

    public bool CoveredByFullscreenApp()
    {
        nint foreground = GetForegroundWindow();
        if (foreground == nint.Zero || foreground == GetShellWindow() || !GetWindowRect(foreground, out var rect)) return false;
        var className = new StringBuilder(64);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
        var bounds = screen.Bounds;
        return rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;
    }

    private void StartRendering()
    {
        if (rendering) return;
        rendering = true; lastFrame = TimeSpan.MinValue;
        CompositionTarget.Rendering += RenderAnimations;
    }

    private void StopRendering()
    {
        if (!rendering) return;
        CompositionTarget.Rendering -= RenderAnimations; rendering = false;
    }

    private void RenderAnimations(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == lastFrame) return;
            lastFrame = frame.RenderingTime;
        }
        double now = animationClock.Elapsed.TotalSeconds;
        wheel?.Advance(now - wheelStart);
        for (int i = closingWheels.Count - 1; i >= 0; i--)
        {
            var closing = closingWheels[i];
            closing.View.Advance(now - closing.Start);
            if (closing.View.Finished) { canvas.Children.Remove(closing.View); closingWheels.RemoveAt(i); }
        }
        for (int i = markers.Count - 1; i >= 0; i--)
        {
            var marker = markers[i];
            double age = now - marker.Start;
            if (age >= marker.Duration)
            {
                canvas.Children.Remove(marker.Element); markers.RemoveAt(i); continue;
            }
            marker.View.Advance(age);
            marker.Element.Opacity = WheelAnimation.Open(age) * PingAnimation.Fade(age, marker.Duration);
            marker.Label.Opacity = WheelAnimation.Open(age - 0.04);
        }
        if (wheel == null && closingWheels.Count == 0 && markers.Count == 0) StopRendering();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint handle, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint handle, out WindowRect rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(nint handle, StringBuilder name, int capacity);
}
