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
    private readonly double[] selection = new double[8];
    private readonly ImageSource?[] icons = new ImageSource?[8];
    private readonly Color[] colors = new Color[8];
    private readonly ImageSource? glow, quarterGlow;
    private static readonly Color FocusColor = Color.FromRgb(246, 134, 40);
    private static readonly Brush White = new SolidColorBrush(Color.FromRgb(242, 242, 244));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(155, 160, 175));

    public WheelView(AssetCatalog assets, NativeImages images)
    {
        Assets = assets; Images = images;
        Width = Height = 380;
        IsHitTestVisible = false;
        glow = images.GetColored(assets.WheelPathFor("glow"), FocusColor);
        quarterGlow = images.GetColored(assets.WheelPathFor("quarterGlow"), FocusColor);
        for (int i = 0; i < Pings.All.Length; i++)
        {
            var ping = Pings.All[i];
            colors[i] = (Color)ColorConverter.ConvertFromString(assets.ColorFor(ping.Id));
            icons[i] = images.GetColored(assets.PathFor(ping.Id, true), colors[i]);
        }
    }

    public void Select(int index)
    {
        if (Selected == index) return;
        Selected = index;
        if (Preview) for (int i = 0; i < selection.Length; i++) selection[i] = i == index ? 1 : 0;
        InvalidateVisual();
    }

    public void Advance(double age)
    {
        double elapsed = Math.Max(0, age - Age); Age = age;
        Opacity = closeAt < 0 ? WheelAnimation.Open(age) : closeOpacity * WheelAnimation.Close(age - closeAt);
        for (int i = 0; i < selection.Length; i++) selection[i] = WheelAnimation.Follow(selection[i], i == Selected ? 1 : 0, elapsed);
        InvalidateVisual();
    }

    public void BeginClose() { closeAt = Age; closeOpacity = Opacity; }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        double factor = Math.Min(ActualWidth, ActualHeight) / 380;
        dc.PushTransform(new ScaleTransform(factor, factor, centre.X, centre.Y));
        var outline = new Pen(new SolidColorBrush(Color.FromArgb(105, 184, 188, 202)), 1);
        for (int i = 0; i < Pings.All.Length; i++)
        {
            var ping = Pings.All[i];
            bool active = i == Selected;
            var color = colors[i];
            double weight = selection[i];
            var sector = Sector(centre, i, 70, 174);
            dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(221, 20, 23, 29)), outline, sector);
            if (weight > 0.001)
            {
                dc.PushOpacity(weight);
                dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(115, FocusColor.R, FocusColor.G, FocusColor.B)), new Pen(new SolidColorBrush(FocusColor), 2), sector);
                if (quarterGlow != null)
                {
                    // shortcut: original glow masks in desktop geometry; replace when the full RUI shader is ported.
                    dc.PushClip(sector); dc.PushOpacity(0.45);
                    for (int quarter = 0; quarter < 4; quarter++)
                    {
                        dc.PushTransform(new RotateTransform(quarter * 90, centre.X, centre.Y));
                        dc.DrawImage(quarterGlow, new Rect(centre.X - 174, centre.Y - 174, 174, 174));
                        dc.Pop();
                    }
                    dc.Pop(); dc.Pop();
                }
                dc.Pop();
            }
            var (x, y) = Pings.Position(i, 121);
            var pos = new Point(centre.X + x, centre.Y + y);
            if (glow != null && weight > 0.001)
            {
                dc.PushOpacity(0.35 * weight);
                dc.DrawImage(glow, new Rect(pos.X - 32, pos.Y - 37, 64, 64));
                dc.Pop();
            }
            var icon = icons[i];
            if (icon != null)
            {
                double ratio = 36.0 * (1 + 0.1 * weight) / Math.Max(icon.Width, icon.Height);
                double width = icon.Width * ratio, height = icon.Height * ratio;
                dc.DrawImage(icon, new Rect(pos.X - width / 2, pos.Y - 5 - height / 2, width, height));
            }
            else Text(dc, "—", new Point(pos.X, pos.Y - 12), 24, Muted);
            Text(dc, ping.Label, new Point(pos.X, pos.Y + 18), 11, active ? White : Muted);
        }
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(244, 16, 19, 25)), outline, centre, 65, 65);
        string title = Selected >= 0 ? Pings.All[Selected].Label : Preview ? "标记轮盘" : "取消";
        string subtitle = Selected >= 0 ? Pings.All[Selected].English : Preview ? "8 种战术标记" : "移到中央 / ESC";
        Text(dc, title, new Point(centre.X, centre.Y - 9), 17, White);
        Text(dc, subtitle, new Point(centre.X, centre.Y + 16), 10, Muted);
        dc.Pop();
    }

    private static StreamGeometry Sector(Point centre, int index, double inner, double outer)
    {
        double start = (index * 45 - 112.5) * Math.PI / 180;
        double end = start + Math.PI / 4;
        Point At(double radius, double angle) => new(centre.X + Math.Cos(angle) * radius, centre.Y + Math.Sin(angle) * radius);
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

    private void Text(DrawingContext dc, string value, Point centre, double size, Brush brush)
    {
        var text = new FormattedText(value, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface("Microsoft YaHei UI"), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }
}
