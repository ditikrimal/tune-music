using System.Windows.Media;
using Tune.App.Platform;

namespace Tune.App.Tests;

public class ThemeTests
{
    private static byte[] Pixels(int count, params (Color C, int Weight)[] parts)
    {
        var list = new List<byte>();
        foreach (var (c, w) in parts)
            for (var i = 0; i < count * w; i++) list.AddRange([c.B, c.G, c.R, 255]);
        return list.ToArray();
    }

    [Fact]
    public void Dominant_color_prefers_colourful_pixels_over_grey()
    {
        // mostly grey/black art with a red subject: the header should be red-ish, not grey
        var px = Pixels(100, (Color.FromRgb(20, 20, 20), 6), (Color.FromRgb(128, 128, 128), 2), (Color.FromRgb(200, 30, 40), 2));
        var c = AlbumColor.Dominant(px);
        Assert.True(c.R > c.G * 2 && c.R > c.B * 2, c.ToString());
    }

    [Fact]
    public void Header_color_is_dark_enough_for_white_text()
    {
        var bright = AlbumColor.ForHeader(Color.FromRgb(250, 230, 80)); // yellow art
        Assert.True(AlbumColor.Luminance(bright) < 0.12, AlbumColor.Luminance(bright).ToString());
        Assert.True(bright.R > bright.B, "keeps the hue");
    }

    [Fact]
    public void Widget_tint_is_a_muted_mid_tone_of_the_art_colour()
    {
        // like WidgetPod: clearly tinted (not near-black like the page header), still readable with white text
        var tint = AlbumColor.ForWidget(Color.FromRgb(120, 170, 200)); // misty blue art
        var lum = AlbumColor.Luminance(tint);
        Assert.InRange(lum, 0.08, 0.2);
        Assert.True(tint.B > tint.R, "keeps the hue");
        Assert.True(AlbumColor.Luminance(AlbumColor.ForWidget(Color.FromRgb(10, 10, 30))) >= 0.02, "very dark art still gets a visible tint");
    }

    [Fact]
    public void All_grey_art_gives_neutral_header()
    {
        var c = AlbumColor.Dominant(Pixels(100, (Color.FromRgb(90, 90, 90), 1)));
        Assert.True(Math.Abs(c.R - c.G) < 10 && Math.Abs(c.G - c.B) < 10);
    }

    [Fact]
    public void Theme_palettes_have_readable_contrast()
    {
        foreach (var bg in Enum.GetValues<BackgroundStyle>())
        foreach (var accent in ThemeManager.Accents)
        {
            var p = ThemeManager.Palette(bg, accent.Color);
            Assert.True(AlbumColor.Luminance(p.Base) < 0.05, $"{bg} base too light");
            Assert.True(AlbumColor.Luminance(p.Card) > AlbumColor.Luminance(p.Base) || bg == BackgroundStyle.Black, $"{bg} cards blend into base");
            Assert.True(AlbumColor.Luminance(accent.Color) > 0.12, $"{accent.Name} too dark to see");
        }
    }
}
