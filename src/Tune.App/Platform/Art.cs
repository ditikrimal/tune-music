using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

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
