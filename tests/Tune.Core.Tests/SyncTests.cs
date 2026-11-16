using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Sync;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

/// <summary>An account in memory: like Firestore, each write gets an increasing server stamp.</summary>
internal sealed class FakeRemote : ISyncRemote
{
    private readonly Dictionary<(string, string), (long Stamp, RemoteDoc Doc)> _docs = [];
    private long _clock;
    public int Pushes;

    public Task PushAsync(IReadOnlyList<RemoteDoc> docs, CancellationToken ct)
    {
        Pushes++;
        var stamp = ++_clock; // one commit = one server time, like REQUEST_TIME
        foreach (var d in docs) _docs[(d.Collection, d.Id)] = (stamp, d);
        return Task.CompletedTask;
    }

    public Task<RemotePage> PullAsync(string collection, string? cursor, int limit, CancellationToken ct)
    {
        (long, string) after = cursor?.Split('|') is [var s, var n] ? (long.Parse(s), n) : (0, "");
        var page = _docs.Where(kv => kv.Key.Item1 == collection)
            .Select(kv => (kv.Value.Stamp, Name: kv.Key.Item2, kv.Value.Doc))
            .Where(x => x.Stamp > after.Item1 || (x.Stamp == after.Item1 && string.CompareOrdinal(x.Name, after.Item2) > 0))
            .OrderBy(x => x.Stamp).ThenBy(x => x.Name, StringComparer.Ordinal).Take(limit).ToList();
        var next = page.Count > 0 ? $"{page[^1].Stamp}|{page[^1].Name}" : cursor;
        return Task.FromResult(new RemotePage(page.Select(x => x.Doc).ToList(), next));
    }
}

public sealed class SyncTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tune-sync-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRemote _account = new();

    public SyncTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private LibraryStore Device(string name) => new(Path.Combine(_dir, name + ".db"));

    private Task Sync(LibraryStore lib, string device) => new SyncEngine(lib, _account, device).SyncAsync();

    private static string Id(string x) => T(x).VideoId;

    [Fact]
    public async Task Reinstall_gets_everything_back()
    {
        using var phone = Device("phone");
        phone.SetLiked(T("a"), true);
        var p = phone.CreatePlaylist("Road trip");
        phone.AddManyToPlaylist(p, [T("b"), T("c")]);
        phone.RecordPlay(T("d"), DateTimeOffset.UtcNow.AddMinutes(-5));
        await Sync(phone, "phone");

        using var fresh = Device("fresh"); // new install: new database, new device id
        await Sync(fresh, "fresh");

        Assert.Equal([Id("a")], fresh.GetLiked().Select(t => t.VideoId));
        var pl = Assert.Single(fresh.GetPlaylists());
        Assert.Equal("Road trip", pl.Name);
        Assert.Equal([Id("b"), Id("c")], fresh.GetPlaylistTracks(pl.Id).Select(t => t.VideoId));
        Assert.Equal([Id("d")], fresh.GetRecent(10).Select(t => t.VideoId));
        Assert.Equal("Title a", fresh.GetLiked()[0].Title);
    }

    [Fact]
    public async Task Changes_flow_both_ways_and_unlikes_and_deletes_travel()
    {
        using var pc = Device("pc");
        using var phone = Device("phone");
        pc.SetLiked(T("a"), true);
        var p = pc.CreatePlaylist("Mix");
        await Sync(pc, "pc");
        await Sync(phone, "phone");
        Assert.Single(phone.GetLiked());

        await Task.Delay(5);
        phone.SetLiked(T("a"), false);
        phone.SetLiked(T("b"), true);
        phone.DeletePlaylist(phone.GetPlaylists().Single().Id);
        await Sync(phone, "phone");
        await Sync(pc, "pc");

        Assert.Equal([Id("b")], pc.GetLiked().Select(t => t.VideoId));
        Assert.Empty(pc.GetPlaylists());
    }

    [Fact]
    public async Task Newest_edit_of_a_playlist_wins()
    {
        using var pc = Device("pc");
        using var phone = Device("phone");
        var p = pc.CreatePlaylist("Mix");
        await Sync(pc, "pc");
        await Sync(phone, "phone");
        var onPhone = phone.GetPlaylists().Single().Id;

        pc.AddToPlaylist(p, T("old"));       // edited first…
        await Task.Delay(5);
        phone.RenamePlaylist(onPhone, "Mix 2"); // …edited later
        phone.AddToPlaylist(onPhone, T("new"));
        await Sync(pc, "pc");
        await Sync(phone, "phone");
        await Sync(pc, "pc");

        foreach (var lib in new[] { pc, phone })
        {
            var pl = lib.GetPlaylists().Single();
            Assert.Equal("Mix 2", pl.Name);
            Assert.Equal([Id("new")], lib.GetPlaylistTracks(pl.Id).Select(t => t.VideoId));
        }
    }

    [Fact]
    public async Task Same_playlist_imported_on_both_devices_becomes_one()
    {
        using var pc = Device("pc");
        using var phone = Device("phone");
        pc.AddManyToPlaylist(pc.CreatePlaylist("Songs to Listen"), [T("a"), T("b")]);
        phone.AddManyToPlaylist(phone.CreatePlaylist("Songs to Listen"), [T("b"), T("c")]);
        phone.CreatePlaylist("Only on phone");
        await Sync(pc, "pc");
        await Sync(phone, "phone");
        await Sync(pc, "pc");

        foreach (var lib in new[] { pc, phone })
        {
            Assert.Equal(["Songs to Listen", "Only on phone"], lib.GetPlaylists().Select(p => p.Name));
            var stl = lib.GetPlaylists().First();
            Assert.Equal([Id("a"), Id("b"), Id("c")], lib.GetPlaylistTracks(stl.Id).Select(t => t.VideoId));
        }
    }

    [Fact]
    public async Task History_merges_without_duplicates_or_echo()
    {
        using var pc = Device("pc");
        using var phone = Device("phone");
        var t0 = DateTimeOffset.UtcNow.AddHours(-1);
        pc.RecordPlay(T("a"), t0);
        phone.RecordPlay(T("b"), t0.AddMinutes(1));
        for (var i = 0; i < 3; i++)
        {
            await Sync(pc, "pc");
            await Sync(phone, "phone");
        }
        foreach (var lib in new[] { pc, phone })
            Assert.Equal([Id("b"), Id("a")], lib.GetRecent(10).Select(t => t.VideoId));
        Assert.Equal(1, pc.GetTopTracks(t0.AddDays(-1), 10).Single(s => s.Track.VideoId == Id("a")).Plays);
        Assert.Equal(1, phone.GetTopTracks(t0.AddDays(-1), 10).Single(s => s.Track.VideoId == Id("a")).Plays);
    }

    [Fact]
    public async Task Nothing_to_do_means_no_writes()
    {
        using var pc = Device("pc");
        pc.SetLiked(T("a"), true);
        await Sync(pc, "pc");
        var pushes = _account.Pushes;
        await Sync(pc, "pc");
        Assert.Equal(pushes, _account.Pushes);
        Assert.Empty(pc.PendingSync());
    }

    [Fact]
    public async Task Existing_data_before_sign_in_is_uploaded_but_loses_to_account_changes()
    {
        using var phone = Device("phone");
        phone.SetLiked(T("a"), true);
        await Sync(phone, "phone");
        await Task.Delay(5);
        phone.SetLiked(T("a"), false); // unliked on the phone…
        await Sync(phone, "phone");

        // …the PC liked it long ago, before it ever signed in (pretend: no change tracking back then)
        using var pc = Device("pc");
        pc.SetLiked(T("a"), true);
        pc.SetLiked(T("z"), true);
        pc.MarkAllForSync();
        ForgetTimes(pc);
        await Sync(pc, "pc");

        Assert.Equal([Id("z")], pc.GetLiked().Select(t => t.VideoId));
    }

    private void ForgetTimes(LibraryStore lib)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(_dir, "pc.db")}");
        db.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE sync_items SET updated_at = 1";
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Song_format_round_trips()
    {
        var t = new Track("abcdefghijk", "Song", [new ArtistRef("A", "UC1"), new ArtistRef("B", null)], new AlbumRef("Al", "MPRE1"),
            TimeSpan.FromSeconds(201), "https://i/x.jpg");
        var back = SyncFormat.ParseTracks(SyncFormat.TracksJson([t])).Single();
        Assert.Equal(t.VideoId, back.VideoId);
        Assert.Equal(t.Title, back.Title);
        Assert.Equal(t.Artists, back.Artists);
        Assert.Equal(t.Album, back.Album);
        Assert.Equal(t.Duration, back.Duration);
        Assert.Equal(t.ThumbnailUrl, back.ThumbnailUrl);
    }

    [Fact]
    public void Config_reads_the_properties_file()
    {
        var c = SyncConfig.Parse("# Tune\nprojectId = tune-music\napiKey=AIza1\r\nwebClientId=1-web.apps\ndesktopClientId=1-desk\n");
        Assert.NotNull(c);
        Assert.Equal("tune-music", c.ProjectId);
        Assert.Equal("1-web.apps", c.WebClientId);
        Assert.Null(c.DesktopClientSecret);
        Assert.Null(SyncConfig.Parse("projectId=x"));
    }
}
