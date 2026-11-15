using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tune.App.Platform;

/// <summary>
/// The Tune logo (equalizer "T"), read from Assets/Tune.ico — the same file is the exe's icon, so the
/// taskbar, Start menu, Explorer, window, tray and sidebar all match. Regenerate it with tools/IconGen.
/// </summary>
public static class AppIcon
{
    private const string Resource = "Tune.ico";

    private static Stream Open() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream(Resource)
        ?? throw new InvalidOperationException("Tune.ico is not embedded");

    /// <summary>All sizes of the icon; WPF picks the right one for title bar and taskbar.</summary>
    public static BitmapFrame Window { get; } = LoadFrame(largest: false);

    /// <summary>The 256 px image, for the in-app logo.</summary>
    public static ImageSource Logo { get; } = LoadFrame(largest: true);

    private static BitmapFrame LoadFrame(bool largest)
    {
        using var s = Open();
        var decoder = new IconBitmapDecoder(s, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = largest ? decoder.Frames.OrderByDescending(f => f.PixelWidth).First() : decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>Tray icon at the requested size (the tray library needs a System.Drawing.Icon).</summary>
    public static System.Drawing.Icon CreateTrayIcon(int size)
    {
        using var s = Open();
        return new System.Drawing.Icon(s, size, size);
    }
}
