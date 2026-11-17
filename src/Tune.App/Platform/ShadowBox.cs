using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace Tune.App.Platform;

/// <summary>
/// A soft shadow of any size from one small pre-blurred picture cut into nine pieces (corners stay sharp-sized,
/// edges stretch). Unlike DropShadowEffect it is never recomputed: things that sit over a scrolling page
/// (the player bar) or repeat on every card stay as cheap as an image.
/// Place it behind the element with a negative margin of <see cref="Blur"/> on each side (plus any downward offset).
/// </summary>
public sealed class ShadowBox : FrameworkElement
{
    public static readonly DependencyProperty BlurProperty = DependencyProperty.Register(nameof(Blur), typeof(double), typeof(ShadowBox),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(nameof(CornerRadius), typeof(double), typeof(ShadowBox),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(Color), typeof(ShadowBox),
        new FrameworkPropertyMetadata(Colors.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>How far the shadow spreads past the element, in px.</summary>
    public double Blur { get => (double)GetValue(BlurProperty); set => SetValue(BlurProperty, value); }
    public double CornerRadius { get => (double)GetValue(CornerRadiusProperty); set => SetValue(CornerRadiusProperty, value); }
    public Color Color { get => (Color)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }

    public ShadowBox() => IsHitTestVisible = false;

    private sealed record Slices(double Edge, BitmapSource[] Parts);
    private static readonly Dictionary<(int, int, Color), Slices> Cache = [];

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 1 || h < 1) return;
        var s = SlicesFor(Blur, CornerRadius, Color);
        // corners keep their size unless the box is too small for them
        var e = Math.Min(s.Edge, Math.Min(w, h) / 2);
        double[] xs = [0, e, w - e, w], ys = [0, e, h - e, h];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
            {
                var rw = xs[col + 1] - xs[col]; var rh = ys[row + 1] - ys[row];
                if (rw <= 0 || rh <= 0) continue;
                // a hair of overlap hides seams between pieces
                dc.DrawImage(s.Parts[row * 3 + col], new Rect(xs[col] - 0.25, ys[row] - 0.25, rw + 0.5, rh + 0.5));
            }
    }

    private static Slices SlicesFor(double blur, double radius, Color color)
    {
        var key = ((int)Math.Round(blur), (int)Math.Round(radius), color);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        double b = key.Item1, r = key.Item2;
        // edge piece = the spread outside + the corner + the blur reaching back inside the shape
        var edge = 2 * b + r;
        const double middle = 4, scale = 2;
        var size = 2 * edge + middle;
        var rect = new Rectangle
        {
            Width = size - 2 * b, Height = size - 2 * b, RadiusX = r, RadiusY = r,
            Fill = new SolidColorBrush(color),
            Effect = b > 0 ? new BlurEffect { Radius = b, KernelType = KernelType.Gaussian } : null,
            Margin = new Thickness(b),
        };
        var host = new System.Windows.Controls.Grid { Width = size, Height = size };
        host.Children.Add(rect);
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        host.UpdateLayout();
        var px = (int)Math.Ceiling(size * scale);
        var bmp = new RenderTargetBitmap(px, px, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(host);
        bmp.Freeze();
        int e = (int)Math.Round(edge * scale), m = px - 2 * e;
        int[] at = [0, e, e + m], len = [e, m, e];
        var parts = new BitmapSource[9];
        for (var row = 0; row < 3; row++)
            for (var col = 0; col < 3; col++)
            {
                var part = new CroppedBitmap(bmp, new Int32Rect(at[col], at[row], len[col], len[row]));
                part.Freeze();
                parts[row * 3 + col] = part;
            }
        return Cache[key] = new Slices(edge, parts);
    }
}
