using System.Windows.Media;
using Tune.App.Platform;

namespace Tune.App.Tests;

public class ColorMathTests
{
    [Theory]
    [InlineData(0, 1, 1, 255, 0, 0)]
    [InlineData(120, 1, 1, 0, 255, 0)]
    [InlineData(240, 1, 1, 0, 0, 255)]
    [InlineData(60, 0.5, 1, 255, 255, 128)]
    [InlineData(0, 0, 0.5, 128, 128, 128)]
    public void FromHsv_matches_known_colours(double h, double s, double v, byte r, byte g, byte b)
    {
        Assert.Equal(Color.FromRgb(r, g, b), ColorMath.FromHsv(h, s, v));
    }

    [Fact]
    public void ToHsv_round_trips()
    {
        var c = Color.FromRgb(0x3B, 0x82, 0xF6);
        var (h, s, v) = ColorMath.ToHsv(c);
        var back = ColorMath.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(back.R - c.R) + Math.Abs(back.G - c.G) + Math.Abs(back.B - c.B), 0, 3);
    }

    [Theory]
    [InlineData("#FF8800", true)]
    [InlineData("ff8800", true)]
    [InlineData("#F80", true)]
    [InlineData("#GG0000", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void TryParseHex(string? text, bool ok)
    {
        Assert.Equal(ok, ColorMath.TryParseHex(text, out var c));
        if (ok) Assert.Equal(Color.FromRgb(0xFF, 0x88, 0x00), c);
    }

    [Fact]
    public void ToHex_is_uppercase_with_hash() =>
        Assert.Equal("#1DB954", ColorMath.ToHex(Color.FromRgb(0x1D, 0xB9, 0x54)));

    [Fact]
    public void Readable_accent_lifts_colours_too_dark_to_see_on_a_dark_ui()
    {
        var lifted = ColorMath.ReadableAccent(Color.FromRgb(10, 0, 40));
        Assert.True(AlbumColor.Luminance(lifted) >= 0.12);
        // already bright colours are left alone
        var orange = Color.FromRgb(0xF9, 0x73, 0x16);
        Assert.Equal(orange, ColorMath.ReadableAccent(orange));
    }

    [Fact]
    public void On_accent_text_is_black_on_light_and_white_on_mid_tones()
    {
        Assert.Equal(Colors.Black, ColorMath.OnAccent(Color.FromRgb(0xFA, 0xCC, 0x15)));
        Assert.Equal(Colors.White, ColorMath.OnAccent(Color.FromRgb(0x4F, 0x2A, 0xD8)));
    }
}
