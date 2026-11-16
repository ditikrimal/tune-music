using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public class PlaylistLinkTests
{
    [Theory]
    [InlineData("https://music.youtube.com/playlist?list=PLJ968k8uWUmXbTohbJAfR2pYiDHo771el&si=SjJYibdrW5YjSuzc&drli=1", "PLJ968k8uWUmXbTohbJAfR2pYiDHo771el")]
    [InlineData("https://www.youtube.com/watch?v=abc&list=PLabcdefghij12", "PLabcdefghij12")]
    [InlineData("  VLPLJ968k8uWUmXbTohbJAfR2pYiDHo771el ", "PLJ968k8uWUmXbTohbJAfR2pYiDHo771el")]
    [InlineData("OLAK5uy_kxyzABCDEFGH123", "OLAK5uy_kxyzABCDEFGH123")]
    public void Finds_the_playlist_id(string input, string expected) => Assert.Equal(expected, PlaylistLinks.Parse(input));

    [Theory]
    [InlineData("")]
    [InlineData("hello world")]
    [InlineData("https://music.youtube.com/watch?v=abcdefghijk")]
    public void Rejects_other_text(string input) => Assert.Null(PlaylistLinks.Parse(input));
}
