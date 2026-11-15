using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tune.App.Platform;

/// <summary>Draws the Tune logo (green circle + note glyph) at any size — used for window, taskbar and tray.</summary>
public static class AppIcon
{
    /// <summary>Window / taskbar icon.</summary>
    public static BitmapImage Create(int size)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(RenderPng(size));
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// Tray icon. The tray library can't convert in-memory WPF images, so hand it a System.Drawing.Icon
    /// built from a single-image .ico container holding the PNG (supported since Windows Vista).
    /// </summary>
    public static System.Drawing.Icon CreateTrayIcon(int size)
    {
        var png = RenderPng(size);
        using var ico = new MemoryStream();
        using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            w.Write((short)0);                       // reserved
            w.Write((short)1);                       // type: icon
            w.Write((short)1);                       // image count
            w.Write((byte)(size >= 256 ? 0 : size)); // width
            w.Write((byte)(size >= 256 ? 0 : size)); // height
            w.Write((byte)0);                        // palette colours
            w.Write((byte)0);                        // reserved
            w.Write((short)1);                       // colour planes
            w.Write((short)32);                      // bits per pixel
            w.Write(png.Length);                     // image size
            w.Write(6 + 16);                         // image offset
            w.Write(png);
        }
        ico.Position = 0;
        return new System.Drawing.Icon(ico, size, size);
    }

    private static byte[] RenderPng(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
            dc.DrawEllipse(green, null, new Point(size / 2.0, size / 2.0), size / 2.0, size / 2.0);
            var glyph = new FormattedText("", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                size * 0.55, Brushes.Black, 1.0);
            dc.DrawText(glyph, new Point((size - glyph.Width) / 2, (size - glyph.Height) / 2));
        }
        var rendered = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rendered));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
