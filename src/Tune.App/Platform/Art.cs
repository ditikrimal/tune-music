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
}
