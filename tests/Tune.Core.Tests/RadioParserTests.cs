using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public class RadioParserTests
{
    [Fact]
    public void Radio_returns_at_least_10_tracks_first_is_seed()
    {
        var tracks = RadioParser.Parse(Fixture.Load("next_radio.json"));
        Assert.True(tracks.Count >= 10, $"tracks: {tracks.Count}");
        Assert.Equal("oyr9_tu_F2U", tracks[0].VideoId);
        Assert.All(tracks, t =>
        {
            Assert.Equal(11, t.VideoId.Length);
            Assert.False(string.IsNullOrWhiteSpace(t.Title));
        });
    }

    [Fact]
    public void Radio_track_has_artists_album_and_duration()
    {
        var t = RadioParser.Parse(Fixture.Load("next_radio.json"))[0];
        Assert.Equal(["Tanishk Bagchi", "Arijit Singh", "Afsana Khan", "Irshad Kamil"], t.Artists.Select(a => a.Name));
        Assert.NotNull(t.Album);
        Assert.StartsWith("MPRE", t.Album!.BrowseId);
        Assert.Equal(TimeSpan.FromSeconds(248), t.Duration);
        Assert.NotNull(t.ThumbnailUrl);
    }
}
