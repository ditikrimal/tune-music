using System.Globalization;
using System.Windows.Media;

namespace Tune.App.Platform;

/// <summary>Colour helpers for the custom accent picker.</summary>
public static class ColorMath
{
    /// <summary>h in degrees [0,360), s and v in [0,1].</summary>
    public static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        v = Math.Clamp(v, 0, 1);
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = (int)(h / 60) switch
        {
            0 => (c, x, 0d),
            1 => (x, c, 0d),
            2 => (0d, c, x),
            3 => (0d, x, c),
            4 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        static byte B(double d) => (byte)Math.Round(Math.Clamp(d, 0, 1) * 255);
        return Color.FromRgb(B(r + m), B(g + m), B(b + m));
    }

    public static (double H, double S, double V) ToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            if (max == r) h = 60 * (((g - b) / d) % 6);
            else if (max == g) h = 60 * ((b - r) / d + 2);
            else h = 60 * ((r - g) / d + 4);
        }
        if (h < 0) h += 360;
        return (h, max <= 0 ? 0 : d / max, max);
    }

    public static bool TryParseHex(string? text, out Color color)
    {
        color = default;
        var t = text?.Trim().TrimStart('#') ?? "";
        if (t.Length == 3) t = string.Concat(t.Select(ch => $"{ch}{ch}"));
        if (t.Length != 6 || !int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        color = Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        return true;
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>An accent has to stand out on a dark UI: very dark picks are lifted toward white.</summary>
    public static Color ReadableAccent(Color c)
    {
        var x = c;
        for (var i = 0; i < 60 && AlbumColor.Luminance(x) < 0.12; i++)
            x = Color.FromRgb((byte)Math.Min(255, x.R + 6), (byte)Math.Min(255, x.G + 6), (byte)Math.Min(255, x.B + 6));
        return x;
    }

    /// <summary>The accent as text on the dark UI: dark/greyish accents are lifted until they read clearly.</summary>
    public static Color AccentText(Color c)
    {
        var x = c;
        for (var i = 0; i < 80 && AlbumColor.Luminance(x) < 0.22; i++)
            x = Color.FromRgb((byte)Math.Min(255, x.R + 5), (byte)Math.Min(255, x.G + 5), (byte)Math.Min(255, x.B + 5));
        return x;
    }

    /// <summary>Text/icon colour on top of the accent: whichever of black/white contrasts more.</summary>
    public static Color OnAccent(Color accent)
    {
        var l = AlbumColor.Luminance(accent);
        return (l + 0.05) / 0.05 >= 1.05 / (l + 0.05) ? Colors.Black : Colors.White;
    }
}
