using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Tune.Core.Library;

namespace Tune.App.Platform;

public enum BackgroundStyle { Dark, Black, Midnight }

/// <summary>What colours the top of each page: nothing (default), the accent, or the playing song's art.</summary>
public enum HeaderTint { None, Accent, AlbumArt }

public sealed record AccentChoice(string Name, Color Color);

public sealed record Palette(Color Base, Color Sidebar, Color Card, Color CardHover, Color PlayerBar, Color Divider, Color Header);

/// <summary>Colour maths for the album-art tinted header.</summary>
public static class AlbumColor
{
    public static double Luminance(Color c)
    {
        static double Lin(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>Average colour weighted by saturation, so a colourful subject beats a grey background.</summary>
    public static Color Dominant(byte[] bgra)
    {
        double r = 0, g = 0, b = 0, w = 0, gr = 0, gg = 0, gb = 0, gn = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4 * 3) // every 3rd pixel is plenty
        {
            double pb = bgra[i], pg = bgra[i + 1], pr = bgra[i + 2];
            var max = Math.Max(pr, Math.Max(pg, pb));
            var min = Math.Min(pr, Math.Min(pg, pb));
            var sat = max <= 0 ? 0 : (max - min) / max;
            var weight = sat * sat * (max / 255.0);
            r += pr * weight; g += pg * weight; b += pb * weight; w += weight;
            gr += pr; gg += pg; gb += pb; gn++;
        }
        if (w < gn * 0.01) // essentially grey art
            return gn == 0 ? Colors.Gray : Color.FromRgb((byte)(gr / gn), (byte)(gg / gn), (byte)(gb / gn));
        return Color.FromRgb((byte)(r / w), (byte)(g / w), (byte)(b / w));
    }

    /// <summary>Same hue, darkened until white text on it is comfortable.</summary>
    public static Color ForHeader(Color c)
    {
        var target = 0.09;
        var f = 1.0;
        var x = c;
        for (var i = 0; i < 30 && Luminance(x) > target; i++)
        {
            f *= 0.88;
            x = Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));
        }
        return x;
    }

    /// <summary>Widget background: same hue, muted to a mid-dark tone (lighter than the page header).</summary>
    public static Color ForWidget(Color c)
    {
        var x = c;
        for (var i = 0; i < 30 && Luminance(x) > 0.16; i++)
            x = Color.FromRgb((byte)(x.R * 0.9), (byte)(x.G * 0.9), (byte)(x.B * 0.9));
        // lift very dark art a little so the card still reads as tinted
        for (var i = 0; i < 30 && Luminance(x) < 0.03; i++)
            x = Color.FromRgb((byte)Math.Min(255, x.R + 6), (byte)Math.Min(255, x.G + 6), (byte)Math.Min(255, x.B + 8));
        return x;
    }

    /// <summary>Featured card colour: vivid like the art, but dark enough for white titles.</summary>
    public static Color ForCard(Color c)
    {
        var x = c;
        for (var i = 0; i < 40 && Luminance(x) > 0.28; i++)
            x = Color.FromRgb((byte)(x.R * 0.92), (byte)(x.G * 0.92), (byte)(x.B * 0.92));
        for (var i = 0; i < 40 && Luminance(x) < 0.08; i++)
            x = Color.FromRgb((byte)Math.Min(255, x.R + 8), (byte)Math.Min(255, x.G + 8), (byte)Math.Min(255, x.B + 10));
        return x;
    }

    public static Color FromBitmap(BitmapSource bmp)
    {
        var small = new FormatConvertedBitmap(new TransformedBitmap(bmp, new ScaleTransform(48.0 / bmp.PixelWidth, 48.0 / bmp.PixelHeight)),
            PixelFormats.Bgra32, null, 0);
        var stride = small.PixelWidth * 4;
        var px = new byte[stride * small.PixelHeight];
        small.CopyPixels(px, stride, 0);
        return Dominant(px);
    }
}

/// <summary>Applies the chosen accent/background live and tints the header from the playing song's art.</summary>
public static class ThemeManager
{
    public static readonly AccentChoice[] Accents =
    [
        new("Green", Color.FromRgb(0x1D, 0xB9, 0x54)),
        new("Blue", Color.FromRgb(0x3B, 0x82, 0xF6)),
        new("Purple", Color.FromRgb(0xA7, 0x8B, 0xFA)),
        new("Pink", Color.FromRgb(0xEC, 0x48, 0x99)),
        new("Orange", Color.FromRgb(0xF9, 0x73, 0x16)),
        new("Red", Color.FromRgb(0xEF, 0x44, 0x44)),
        new("Teal", Color.FromRgb(0x14, 0xB8, 0xA6)),
        new("Yellow", Color.FromRgb(0xFA, 0xCC, 0x15)),
        new("Indigo", Color.FromRgb(0x81, 0x8C, 0xF8)),
        new("Silver", Color.FromRgb(0xD4, 0xD4, 0xD8)),
    ];

    /// <summary>A colour the user picked freely (kept visible on a dark UI).</summary>
    public static AccentChoice CustomAccent(Color c) => new("Custom", ColorMath.ReadableAccent(c));

    public static Palette Palette(BackgroundStyle bg, Color accent) => bg switch
    {
        BackgroundStyle.Black => new(C(0x00, 0x00, 0x00), C(0x00, 0x00, 0x00), C(0x0E, 0x0E, 0x0E), C(0x1C, 0x1C, 0x1C), C(0x00, 0x00, 0x00), C(0x1A, 0x1A, 0x1A), Tint(accent, 0.18)),
        BackgroundStyle.Midnight => new(C(0x0B, 0x10, 0x20), C(0x05, 0x08, 0x12), C(0x13, 0x1A, 0x2E), C(0x1E, 0x27, 0x42), C(0x0E, 0x14, 0x26), C(0x1E, 0x27, 0x42), C(0x1C, 0x24, 0x4A)),
        _ => new(C(0x12, 0x12, 0x12), C(0x00, 0x00, 0x00), C(0x18, 0x18, 0x18), C(0x28, 0x28, 0x28), C(0x18, 0x18, 0x18), C(0x28, 0x28, 0x28), C(0x2A, 0x21, 0x40)),
    };

    public static AccentChoice Accent { get; private set; } = Accents[0];
    public static BackgroundStyle Background { get; private set; }
    public static HeaderTint Header { get; private set; } = HeaderTint.None;

    /// <summary>Windows 11 acrylic behind the window; the sidebar becomes see-through glass.</summary>
    public static bool Translucent { get; private set; } = true;

    public static event Action? TranslucencyChanged;

    public static void Load(LibraryStore lib)
    {
        var saved = lib.GetSetting("theme.accent");
        Accent = Accents.FirstOrDefault(a => a.Name == saved)
                 ?? (ColorMath.TryParseHex(saved, out var custom) ? CustomAccent(custom) : Accents[0]);
        Background = Enum.TryParse<BackgroundStyle>(lib.GetSetting("theme.background"), out var bg) ? bg : BackgroundStyle.Dark;
        Header = Enum.TryParse<HeaderTint>(lib.GetSetting("theme.header"), out var h) ? h : HeaderTint.None;
        Translucent = lib.GetSetting("theme.translucent") != "0";
        Apply();
    }

    public static void Set(LibraryStore lib, AccentChoice? accent = null, BackgroundStyle? background = null, HeaderTint? header = null, bool? translucent = null, bool persist = true)
    {
        if (translucent is not null)
        {
            Translucent = translucent.Value;
            lib.SetSetting("theme.translucent", translucent.Value ? "1" : "0");
            TranslucencyChanged?.Invoke();
        }
        if (accent is not null) { Accent = accent; if (persist) SaveAccent(lib); }
        if (background is not null) { Background = background.Value; lib.SetSetting("theme.background", background.Value.ToString()); }
        if (header is not null) { Header = header.Value; lib.SetSetting("theme.header", header.Value.ToString()); }
        Apply();
    }

    public static void SaveAccent(LibraryStore lib) =>
        lib.SetSetting("theme.accent", Accent.Name == "Custom" ? ColorMath.ToHex(Accent.Color) : Accent.Name);

    private static Color _artHeader = Colors.Transparent;

    /// <summary>Called when the playing song changes: tints the page header with the art's colour.</summary>
    public static void SetArt(BitmapSource? art)
    {
        _artHeader = art is null ? Colors.Transparent : AlbumColor.ForHeader(AlbumColor.FromBitmap(art));
        SetColor("HeaderColor", CurrentHeader(), animate: true);
    }

    private static Color CurrentHeader() => Header switch
    {
        HeaderTint.AlbumArt when _artHeader != Colors.Transparent => _artHeader,
        HeaderTint.AlbumArt or HeaderTint.Accent => Tint(Accent.Color, 0.32),
        _ => Colors.Transparent, // no tint: the page is one consistent surface
    };

    private static void Apply()
    {
        var p = Palette(Background, Accent.Color);
        SetBrush("BaseBrush", p.Base);
        SetColor("BaseColor", p.Base);
        // one surface for the whole window: sidebar and pages share it (frosted when translucent)
        var glass = Translucent && Backdrop.Supported;
        SetBrush("WindowBrush", glass ? Color.FromArgb(0xC8, p.Base.R, p.Base.G, p.Base.B) : p.Base);
        SetBrush("SidebarBrush", p.Sidebar);
        SetBrush("CardBrush", p.Card);
        SetBrush("CardHoverBrush", p.CardHover);
        SetBrush("PlayerBarBrush", p.PlayerBar);
        SetBrush("DividerBrush", p.Divider);
        SetBrush("AccentBrush", Accent.Color);
        SetColor("AccentColor", Accent.Color);
        SetBrush("AccentSoftBrush", Color.FromArgb(0x2E, Accent.Color.R, Accent.Color.G, Accent.Color.B));
        SetBrush("OnAccentBrush", ColorMath.OnAccent(Accent.Color));
        SetBrush("AccentTextBrush", ColorMath.AccentText(Accent.Color));
        SetBrush("AccentBorderBrush", Color.FromArgb(0x55, Accent.Color.R, Accent.Color.G, Accent.Color.B));
        SetColor("HeaderColor", CurrentHeader(), animate: true);
    }

    private static void SetBrush(string key, Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        Application.Current.Resources[key] = b;
    }

    private static void SetColor(string key, Color c, bool animate = false)
    {
        // colours are swapped (DynamicResource); a short fade comes from the gradient brush in the window
        Application.Current.Resources[key] = c;
        if (animate) HeaderChanged?.Invoke(c);
    }

    /// <summary>Raised with the new header colour so the window can fade to it.</summary>
    public static event Action<Color>? HeaderChanged;

    private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static Color Tint(Color accent, double amount) =>
        Color.FromRgb((byte)(accent.R * amount), (byte)(accent.G * amount), (byte)(accent.B * amount));
}
