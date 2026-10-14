using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ApexPing;

public sealed class NativeImages
{
    private readonly Dictionary<string, BitmapSource?> cache = new();
    private readonly Dictionary<(string, Color), ImageSource> colored = new();
    public string? Error { get; private set; }

    public BitmapSource? Get(string? path)
    {
        if (path == null) return null;
        if (cache.TryGetValue(path, out var cached)) return cached;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            cache[path] = image;
            return image;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            cache[path] = null;
            Error = "原生图标无法读取：" + Path.GetFileName(path) + " — " + e.Message;
            return null;
        }
    }

    public ImageSource? GetColored(string? path, Color color)
    {
        var image = Get(path);
        if (image == null || path == null) return null;
        if (colored.TryGetValue((path, color), out var cached)) return cached;
        // Native ping textures are alpha masks; the game's settings supply their color.
        var drawing = new DrawingGroup { OpacityMask = new ImageBrush(image) };
        using (var dc = drawing.Open()) dc.DrawRectangle(new SolidColorBrush(color), null, new Rect(0, 0, image.PixelWidth, image.PixelHeight));
        var tinted = new DrawingImage(drawing); tinted.Freeze();
        colored[(path, color)] = tinted;
        return tinted;
    }
}

public sealed class WheelView : FrameworkElement
{
    public AssetCatalog Assets { get; }
    public NativeImages Images { get; }
    public int Selected { get; private set; } = -1;
    public bool Preview { get; set; }
    public double Age { get; private set; }
    public bool Finished => closeAt >= 0 && Age - closeAt >= 0.75;
    private double closeAt = -1, closeOpacity;
    private bool previewRendering;
    private TimeSpan lastPreviewFrame;
    private readonly double[] selection = new double[8];
    private readonly ImageSource?[] icons = new ImageSource?[8], glows = new ImageSource?[8];
    private readonly StreamGeometry[] sectors = new StreamGeometry[8], rims = new StreamGeometry[8];
    private readonly Brush[] highlights = new Brush[8], inks = new Brush[8];
    private static readonly Brush White = new SolidColorBrush(Color.FromRgb(245, 247, 250));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(174, 184, 198));
    private static readonly Pen Hairline = new(new SolidColorBrush(Color.FromArgb(28, 226, 234, 245)), 0.8);
    private static readonly Brush Backdrop = new RadialGradientBrush(Color.FromArgb(243, 43, 48, 57), Color.FromArgb(237, 20, 23, 29));
    private static readonly Brush SectorFill = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255));
    private static readonly Brush CentreFill = new SolidColorBrush(Color.FromArgb(252, 23, 26, 32));
    private static readonly Brush SoftShadow = new SolidColorBrush(Color.FromArgb(20, 0, 0, 0));
    private static readonly Brush TightShadow = new SolidColorBrush(Color.FromArgb(45, 0, 0, 0));

    public WheelView(AssetCatalog assets, NativeImages images)
    {
        Assets = assets; Images = images;
        Width = Height = 380;
        IsHitTestVisible = false;
        for (int i = 0; i < Pings.All.Length; i++)
        {
            var ping = Pings.All[i];
            var color = (Color)ColorConverter.ConvertFromString(assets.ColorFor(ping.Id));
            icons[i] = images.GetColored(assets.PathFor(ping.Id, true), color);
            glows[i] = images.GetColored(assets.WheelPathFor("glow"), color);
            sectors[i] = Sector(i, 71, 174);
            rims[i] = Sector(i, 171, 174);
            inks[i] = new SolidColorBrush(color); inks[i].Freeze();
            highlights[i] = new RadialGradientBrush(
                Color.FromArgb(10, color.R, color.G, color.B), Color.FromArgb(75, color.R, color.G, color.B));
            highlights[i].Freeze();
        }
        Unloaded += (_, _) => StopPreviewAnimation();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) StopPreviewAnimation();
            else if (Preview)
            {
                for (int i = 0; i < selection.Length; i++) selection[i] = i == Selected ? 1 : 0;
                InvalidateVisual();
            }
        };
    }

    public void Select(int index)
    {
        if (index < -1 || index >= Pings.All.Length) index = -1;
        if (Selected == index) return;
        Selected = index;
        if (Preview)
        {
            if (IsLoaded && IsVisible)
            {
                if (!previewRendering)
                {
                    previewRendering = true; lastPreviewFrame = TimeSpan.Zero;
                    CompositionTarget.Rendering += AnimatePreview;
                }
            }
            else for (int i = 0; i < selection.Length; i++) selection[i] = i == index ? 1 : 0;
        }
        InvalidateVisual();
    }

    public void Advance(double age)
    {
        double elapsed = Math.Max(0, age - Age); Age = age;
        Opacity = Preview ? 1 : closeAt < 0 ? WheelAnimation.Open(age) : closeOpacity * WheelAnimation.Close(age - closeAt);
        for (int i = 0; i < selection.Length; i++) selection[i] = WheelAnimation.Follow(selection[i], i == Selected ? 1 : 0, elapsed);
        InvalidateVisual();
    }

    private void AnimatePreview(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        double elapsed = lastPreviewFrame == TimeSpan.Zero ? 1.0 / 60 : (frame.RenderingTime - lastPreviewFrame).TotalSeconds;
        lastPreviewFrame = frame.RenderingTime;
        Advance(Age + Math.Max(0, elapsed));
        bool moving = false;
        for (int i = 0; i < selection.Length; i++) moving |= Math.Abs(selection[i] - (i == Selected ? 1 : 0)) > 0.001;
        if (!moving) StopPreviewAnimation();
    }

    private void StopPreviewAnimation()
    {
        if (!previewRendering) return;
        CompositionTarget.Rendering -= AnimatePreview; previewRendering = false;
    }

    public void BeginClose() { closeAt = Age; closeOpacity = Opacity; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        double entrance = Preview ? 1 : WheelAnimation.Open(Age);
        double factor = Math.Min(ActualWidth, ActualHeight) / 380 * (0.94 + 0.06 * entrance);
        dc.PushTransform(new TranslateTransform(ActualWidth / 2, ActualHeight / 2));
        dc.PushTransform(new ScaleTransform(factor, factor));
        var centre = new Point();
        dc.DrawEllipse(SoftShadow, null, new Point(0, 4), 182, 182);
        dc.DrawEllipse(TightShadow, null, new Point(0, 3), 178, 178);
        dc.DrawEllipse(Backdrop, Hairline, centre, 174, 174);
        for (int i = 0; i < Pings.All.Length; i++)
        {
            double weight = selection[i];
            dc.DrawGeometry(SectorFill, Hairline, sectors[i]);
            if (weight > 0.001)
            {
                dc.PushOpacity(weight);
                dc.DrawGeometry(highlights[i], null, sectors[i]);
                dc.DrawGeometry(inks[i], null, rims[i]);
                dc.Pop();
            }
            var (x, y) = Pings.Position(i, 122 + 3 * weight);
            var pos = new Point(x, y);
            if (glows[i] is { } glow && weight > 0.001)
            {
                dc.PushOpacity(0.16 * weight);
                dc.DrawImage(glow, new Rect(pos.X - 34, pos.Y - 39, 68, 68));
                dc.Pop();
            }
            if (icons[i] is { } icon)
            {
                double ratio = (32 + 4 * weight) / Math.Max(icon.Width, icon.Height);
                double width = icon.Width * ratio, height = icon.Height * ratio;
                dc.PushOpacity(0.86 + 0.14 * weight);
                dc.DrawImage(icon, new Rect(pos.X - width / 2, pos.Y - 7 - height / 2, width, height));
                dc.Pop();
            }
            else Text(dc, "—", new Point(pos.X, pos.Y - 10), 20, Muted);
            Text(dc, Pings.All[i].Label, new Point(pos.X, pos.Y + 19), 11, i == Selected ? White : Muted);
        }
        dc.DrawEllipse(CentreFill, Hairline, centre, 65, 65);
        dc.DrawRoundedRectangle(Selected >= 0 ? inks[Selected] : Muted, null, new Rect(-10, -33, 20, 2), 1, 1);
        string title = Selected >= 0 ? Pings.All[Selected].Label : Preview ? "APEX" : "取消";
        string subtitle = Selected >= 0 ? Pings.All[Selected].English : Preview ? "拖动选择" : "中央 / ESC";
        Text(dc, title, new Point(0, -7), 16, White, true);
        Text(dc, subtitle, new Point(0, 17), 10, Muted);
        dc.Pop(); dc.Pop();
    }

    private static StreamGeometry Sector(int index, double inner, double outer)
    {
        const double gap = 1.1;
        double start = (index * 45 - 112.5 + gap) * Math.PI / 180;
        double end = ((index + 1) * 45 - 112.5 - gap) * Math.PI / 180;
        Point At(double radius, double angle) => new(Math.Cos(angle) * radius, Math.Sin(angle) * radius);
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(At(inner, start), true, true);
            g.LineTo(At(outer, start), true, false);
            g.ArcTo(At(outer, end), new Size(outer, outer), 0, false, SweepDirection.Clockwise, true, false);
            g.LineTo(At(inner, end), true, false);
            g.ArcTo(At(inner, start), new Size(inner, inner), 0, false, SweepDirection.Counterclockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    private void Text(DrawingContext dc, string value, Point centre, double size, Brush brush, bool bold = false)
    {
        var typeface = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }
}
