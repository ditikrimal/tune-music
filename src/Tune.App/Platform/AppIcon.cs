using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tune.App.Platform;

/// <summary>Draws the Tune logo (green circle + note glyph) at any size — used for window, taskbar and tray.</summary>
public static class AppIcon
{
    public static BitmapSource Create(int size)
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
        var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(visual);
        bmp.Freeze();
        return bmp;
    }
}
