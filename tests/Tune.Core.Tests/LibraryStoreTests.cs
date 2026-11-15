using Tune.Core.Library;
using Tune.Core.Models;

namespace Tune.Core.Tests;

public class LibraryStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("tune-test").FullName;
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    internal static Track T(string id, string artist = "Artist", string? artistId = null) =>
        new(id.PadRight(11, '_'), "Title " + id, [new ArtistRef(artist, artistId)], null, TimeSpan.FromMinutes(3), null);

    private LibraryStore Open() => new(Path.Combine(_dir, "tune.db"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Recent_is_unique_newest_first()
    {
        using var lib = Open();
        lib.RecordPlay(T("a"), T0);
        lib.RecordPlay(T("b"), T0.AddMinutes(1));
        lib.RecordPlay(T("a"), T0.AddMinutes(2));
        Assert.Equal([T("a").VideoId, T("b").VideoId], lib.GetRecent(10).Select(t => t.VideoId));
        Assert.Single(lib.GetRecent(1));
    }

    [Fact]
    public void TopTracks_counts_within_window()
    {
        using var lib = Open();
        lib.RecordPlay(T("old"), T0.AddDays(-30));
        lib.RecordPlay(T("old"), T0.AddDays(-30));
        lib.RecordPlay(T("old"), T0.AddDays(-30));
        lib.RecordPlay(T("a"), T0);
        lib.RecordPlay(T("a"), T0);
        lib.RecordPlay(T("b"), T0);
        var top = lib.GetTopTracks(T0.AddDays(-14), 5);
        Assert.Equal([(T("a").VideoId, 2), (T("b").VideoId, 1)], top.Select(s => (s.Track.VideoId, s.Plays)));
    }

    [Fact]
    public void TopArtists_aggregates_across_tracks()
    {
        using var lib = Open();
        lib.RecordPlay(T("a", "Arijit", "UC1"), T0);
        lib.RecordPlay(T("b", "Arijit", "UC1"), T0);
        lib.RecordPlay(T("c", "Sajjan", "UC2"), T0);
        var top = lib.GetTopArtists(T0.AddDays(-1), 5);
        Assert.Equal(("Arijit", 2), (top[0].Artist.Name, top[0].Plays));
        Assert.Equal("UC1", top[0].Artist.BrowseId);
        Assert.Equal(2, top.Count);
    }

    [Fact]
    public void Like_roundtrip_and_order()
    {
        using var lib = Open();
        lib.SetLiked(T("a"), true);
        lib.SetLiked(T("b"), true);
        Assert.True(lib.IsLiked(T("a").VideoId));
        Assert.Equal([T("b").VideoId, T("a").VideoId], lib.GetLiked().Select(t => t.VideoId));
        lib.SetLiked(T("a"), false);
        Assert.False(lib.IsLiked(T("a").VideoId));
        Assert.Single(lib.GetLiked());
    }

    [Fact]
    public void Playlist_add_move_remove_keeps_positions_contiguous()
    {
        using var lib = Open();
        var id = lib.CreatePlaylist("Night drive");
        lib.AddToPlaylist(id, T("a"));
        lib.AddToPlaylist(id, T("b"));
        lib.AddToPlaylist(id, T("c"));
        lib.MoveInPlaylist(id, 0, 2);
        Assert.Equal([T("b").VideoId, T("c").VideoId, T("a").VideoId], lib.GetPlaylistTracks(id).Select(t => t.VideoId));
        lib.RemoveFromPlaylist(id, 1);
        Assert.Equal([T("b").VideoId, T("a").VideoId], lib.GetPlaylistTracks(id).Select(t => t.VideoId));
        lib.AddToPlaylist(id, T("d"));
        Assert.Equal([T("b").VideoId, T("a").VideoId, T("d").VideoId], lib.GetPlaylistTracks(id).Select(t => t.VideoId));
        var info = Assert.Single(lib.GetPlaylists());
        Assert.Equal(("Night drive", 3), (info.Name, info.Count));
    }

    [Fact]
    public void Rename_and_delete_playlist_removes_items()
    {
        using var lib = Open();
        var id = lib.CreatePlaylist("x");
        lib.AddToPlaylist(id, T("a"));
        lib.RenamePlaylist(id, "Gym");
        Assert.Equal("Gym", lib.GetPlaylists().Single().Name);
        lib.DeletePlaylist(id);
        Assert.Empty(lib.GetPlaylists());
        Assert.Empty(lib.GetPlaylistTracks(id));
    }

    [Fact]
    public void Settings_roundtrip()
    {
        using var lib = Open();
        Assert.Null(lib.GetSetting("k"));
        lib.SetSetting("k", "1");
        lib.SetSetting("k", "2");
        Assert.Equal("2", lib.GetSetting("k"));
    }

    [Fact]
    public void Data_survives_reopen_with_unicode()
    {
        var track = new Track("abcdefghijk", "सम्झनामा", [new ArtistRef("सज्जन राज वैद्य", "UCx")], new AlbumRef("Album", "MPREx"), null, "http://img");
        using (var lib = Open())
        {
            lib.SetLiked(track, true);
            lib.SetSetting("s", "v");
        }
        using var again = Open();
        Assert.Equal(track.Title, again.GetLiked().Single().Title);
        Assert.Equal(track.Artists, again.GetLiked().Single().Artists);
        Assert.Equal("v", again.GetSetting("s"));
    }
}
