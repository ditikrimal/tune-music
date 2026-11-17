using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tune.App.Platform;

/// <summary>
/// Attached property: <c>platform:Art.Url="{Binding ThumbnailUrl}"</c> on an Image loads the art
/// asynchronously through <see cref="ArtCache"/>, decoded at the element's size × DPI.
/// </summary>
public static class Art
{
    public static ArtCache? Cache { get; set; }

    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url", typeof(string), typeof(Art), new PropertyMetadata(null, OnUrlChanged));

    public static string? GetUrl(DependencyObject d) => (string?)d.GetValue(UrlProperty);
    public static void SetUrl(DependencyObject d, string? value) => d.SetValue(UrlProperty, value);

    private static async void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img || Cache is null) return;
        img.Source = null;
        if (e.NewValue is not string url || url.Length == 0) return;

        var logical = double.IsNaN(img.Width) ? 180 : img.Width;
        var dpi = VisualTreeHelper.GetDpi(img).DpiScaleX;
        var px = (int)Math.Ceiling(logical * (dpi <= 0 ? 1 : dpi));
        var bmp = await Cache.GetAsync(url, px);
        if (GetUrl(img) == url) img.Source = bmp; // still showing the same item (virtualized rows get reused)
    }

    // ---------- Art.BlurredUrl: the art as a soft colour glow, blurred once at thumbnail size ----------

    private static readonly Dictionary<string, BitmapSource> Blurred = [];

    /// <summary>
    /// Shows the art heavily blurred. The blur is done once on a 64-px copy and the result is simply stretched:
    /// it looks the same as a live BlurEffect on the full image but costs nothing per frame (and pictures of the
    /// window, e.g. for transitions, don't have to re-blur a full-screen image in software).
    /// </summary>
    public static readonly DependencyProperty BlurredUrlProperty = DependencyProperty.RegisterAttached(
        "BlurredUrl", typeof(string), typeof(Art), new PropertyMetadata(null, OnBlurredUrlChanged));

    public static string? GetBlurredUrl(DependencyObject d) => (string?)d.GetValue(BlurredUrlProperty);
    public static void SetBlurredUrl(DependencyObject d, string? value) => d.SetValue(BlurredUrlProperty, value);

    private static async void OnBlurredUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image img || Cache is null) return;
        if (e.NewValue is not string url || url.Length == 0) { img.Source = null; return; }
        if (!Blurred.TryGetValue(url, out var glow))
        {
            var bmp = await Cache.GetAsync(url, 64);
            if (bmp is null) return;
            glow = Blur(bmp);
            if (Blurred.Count > 30) Blurred.Clear();
            Blurred[url] = glow;
        }
        if (GetBlurredUrl(img) == url) img.Source = glow;
    }

    private static BitmapSource Blur(BitmapSource src)
    {
        const int size = 64;
        // drawn a little larger than the canvas so the blur doesn't fade in from transparent edges
        var image = new Image { Source = src, Stretch = Stretch.UniformToFill, Width = size * 1.4, Height = size * 1.4,
            Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 9, KernelType = System.Windows.Media.Effects.KernelType.Gaussian } };
        var host = new Canvas { Width = size, Height = size, ClipToBounds = true };
        Canvas.SetLeft(image, -size * 0.2); Canvas.SetTop(image, -size * 0.2);
        host.Children.Add(image);
        host.Measure(new Size(size, size));
        host.Arrange(new Rect(0, 0, size, size));
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(host);
        rtb.Freeze();
        return rtb;
    }

    // ---------- Art.TintFrom: paints a Border with the art's own colour (featured cards) ----------

    private static readonly Dictionary<string, Brush> Tints = [];

    public static readonly DependencyProperty TintFromProperty = DependencyProperty.RegisterAttached(
        "TintFrom", typeof(string), typeof(Art), new PropertyMetadata(null, OnTintFromChanged));

    public static string? GetTintFrom(DependencyObject d) => (string?)d.GetValue(TintFromProperty);
    public static void SetTintFrom(DependencyObject d, string? value) => d.SetValue(TintFromProperty, value);

    private static async void OnTintFromChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Border border || Cache is null || e.NewValue is not string url || url.Length == 0) return;
        if (!Tints.TryGetValue(url, out var brush))
        {
            var bmp = await Cache.GetAsync(url, 48);
            if (bmp is null) return;
            var b = new SolidColorBrush(AlbumColor.ForCard(AlbumColor.FromBitmap(bmp)));
            b.Freeze();
            Tints[url] = brush = b;
        }
        if (GetTintFrom(border) == url) border.Background = brush;
    }
}
