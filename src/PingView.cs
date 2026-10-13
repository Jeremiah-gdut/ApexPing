using System;
using System.Windows;
using System.Windows.Media;

namespace ApexPing;

public sealed class PingView : FrameworkElement
{
    private readonly NativeImages images;
    private readonly Color color;
    private readonly string? icon, shadow, outline;
    public double Age { get; private set; }

    public PingView(int index, AssetCatalog assets, NativeImages images)
    {
        this.images = images; string id = Pings.All[index].Id;
        color = (Color)ColorConverter.ConvertFromString(assets.ColorFor(id));
        icon = assets.PathFor(id, true); shadow = assets.LayerPathFor(id, "shadow");
        outline = assets.LayerPathFor(id, "animation");
        Width = Height = 150; IsHitTestVisible = false;
    }

    public void Advance(double seconds) { Age = seconds; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        var frame = PingAnimation.At(Age);
        var centre = new Point(ActualWidth / 2, ActualHeight / 2);
        // shortcut: entrance settling is a desktop adjustment; retain the native layer timelines below.
        double settle = 0.86 + 0.14 * WheelAnimation.Open(Age);
        dc.PushTransform(new ScaleTransform(settle, settle, centre.X, centre.Y));
        void Layer(string? path, double size, double opacity, Color tint)
        {
            if (path == null || opacity <= 0) return;
            var bitmap = images.Get(path);
            if (bitmap == null) return;
            double ratio = size / Math.Max(bitmap.PixelWidth, bitmap.PixelHeight);
            double w = bitmap.PixelWidth * ratio, h = bitmap.PixelHeight * ratio;
            dc.PushOpacity(opacity);
            dc.DrawImage(images.GetColored(path, tint), new Rect(centre.X - w / 2, centre.Y - h / 2, w, h));
            dc.Pop();
        }
        // shortcut: desktop uses the expanded native marker state; add projection/focus blending for a full RUI renderer.
        Layer(outline, 48 * frame.PulseScale, frame.PulseOpacity, color);
        Layer(outline, 48 * frame.SecondPulseScale, frame.SecondPulseOpacity, color);
        Layer(shadow, 72, 0.75, Colors.Black);
        Layer(icon, 72, 1, color);
        Layer(shadow, 48 * frame.BurstScale, frame.BurstOpacity, Colors.Black);
        Layer(icon, 48 * frame.BurstScale, frame.BurstOpacity, color);
        dc.Pop();
    }
}
