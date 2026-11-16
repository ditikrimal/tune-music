using System.Net;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Offline;
using Tune.Core.Update;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

public class ReleaseFeedTests
{
    private const string Json = """
        {"tag_name":"v1.3.0","name":"Tune 1.3.0","html_url":"https://github.com/x/y/releases/tag/v1.3.0","body":"notes",
         "draft":false,"prerelease":false,
         "assets":[{"name":"Tune.apk","browser_download_url":"https://e/Tune.apk","size":7000000},
                   {"name":"Tune.exe","browser_download_url":"https://e/Tune.exe","size":136000000}]}
        """;

    [Fact]
    public void Finds_the_version_and_the_right_file()
    {
        var r = ReleaseFeed.Parse(Json, "Tune.exe")!;
        Assert.Equal(new Version(1, 3, 0), r.Version);
        Assert.Equal("https://e/Tune.exe", r.AssetUrl);
        Assert.Equal(136000000, r.AssetBytes);
        Assert.Equal("https://e/Tune.apk", ReleaseFeed.Parse(Json, "Tune.apk")!.AssetUrl);
        Assert.Null(ReleaseFeed.Parse(Json, "Other.zip")!.AssetUrl);
    }

    [Fact]
    public void Ignores_drafts_prereleases_and_junk()
    {
        Assert.Null(ReleaseFeed.Parse(Json.Replace("\"prerelease\":false", "\"prerelease\":true"), "Tune.exe"));
        Assert.Null(ReleaseFeed.Parse("{\"message\":\"API rate limit exceeded\"}", "Tune.exe"));
        Assert.Null(ReleaseFeed.Parse("not json", "Tune.exe"));
    }

    [Theory]
    [InlineData("v1.3.0", "1.2.0.0", true)]
    [InlineData("v1.2.0", "1.2.0.0", false)] // same version (assembly versions have a 4th part)
    [InlineData("v1.2.1", "1.2.0", true)]
    [InlineData("1.10.0", "1.9.0", true)]   // numbers, not text
    [InlineData("v1.1.0", "1.2.0", false)]
    [InlineData("v2.0", "1.9.9", true)]
    public void Compares_versions_as_numbers(string tag, string current, bool newer) =>
        Assert.Equal(newer, ReleaseFeed.IsNewer(ReleaseFeed.ParseVersion(tag)!, Version.Parse(current)));
}

public class SmartDownloadTests : IDisposable
{
    private static string Id(string s) => T(s).VideoId;
    private readonly LibraryStore _lib = new(":memory:");

    public void Dispose() => _lib.Dispose();

    [Fact]
    public void Recent_favourites_beat_old_ones_and_likes_count()
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1000);
        var ranked = SmartPicker.Rank(
            [(T("old"), 10, now.AddDays(-80)), (T("new"), 4, now.AddDays(-1)), (T("once"), 1, now.AddDays(-2))],
            [T("liked")], now);
        Assert.Equal([Id("new"), Id("old"), Id("liked"), Id("once")], ranked.Select(t => t.VideoId));
    }

    [Fact]
    public void Smart_flag_is_kept_and_asking_for_a_song_yourself_claims_it()
    {
        var at = DateTimeOffset.UnixEpoch;
        _lib.AddDownloads([T("a"), T("b")], at, smart: true);
        _lib.AddDownloads([T("c")], at);
        Assert.Equal(new HashSet<string> { Id("a"), Id("b") }, _lib.SmartDownloadIds());

        _lib.AddDownloads([T("a")], at, smart: false); // already listed: keeps its row...
        Assert.Contains(Id("a"), _lib.SmartDownloadIds());
        _lib.ClaimDownloads([Id("a")]);                    // ...until the user asks for it
        Assert.Equal(new HashSet<string> { Id("b") }, _lib.SmartDownloadIds());
    }

    [Fact]
    public void Play_stats_count_plays_and_remember_the_last_one()
    {
        var t0 = DateTimeOffset.UnixEpoch.AddDays(100);
        _lib.RecordPlay(T("a"), t0);
        _lib.RecordPlay(T("a"), t0.AddHours(5));
        _lib.RecordPlay(T("b"), t0.AddHours(1));
        _lib.RecordPlay(T("c"), t0.AddDays(-200)); // too old
        var stats = _lib.GetPlayStats(t0.AddDays(-90)).ToDictionary(s => s.Track.VideoId);
        Assert.Equal(2, stats.Count);
        Assert.Equal(2, stats[Id("a")].Plays);
        Assert.Equal(t0.AddHours(5), stats[Id("a")].LastPlayed);
    }
}

public class AudioCacheTests : IDisposable
{
    private static string Id(string s) => T(s).VideoId;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tune-cache-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _lib = new(":memory:");

    public void Dispose()
    {
        _lib.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private sealed class Server(int size) : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var r = request.Headers.Range!.Ranges.Single();
            var from = (int)r.From!.Value;
            var to = (int)Math.Min(r.To!.Value, size - 1);
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(new byte[to - from + 1]) };
            resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, size);
            return Task.FromResult(resp);
        }
    }

    private sealed class Resolver(int size) : Tune.Core.Streams.IStreamResolver
    {
        public Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult(new Uri($"https://rr.googlevideo.com/videoplayback?id={videoId}&mime=audio%2Fwebm&clen={size}"));
        public Task PrepareAsync(string videoId, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsReady(string videoId) => true;
        public bool IsKnown(string videoId) => true;
        public void Invalidate(string videoId) { }
        public bool ReportBroken(string videoId) => false;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition());
    }

    [Fact]
    public async Task Keeps_streamed_songs_for_offline_and_drops_the_oldest_when_full()
    {
        const int size = 1000;
        var server = new Server(size);
        using var cache = new AudioCache(_lib, new Resolver(size), new HttpClient(server), _dir, new FakeNetwork(), _ => false);
        _lib.SetSetting("cache.maxBytes", "2500"); // room for two songs
        cache.Start();

        cache.Want([T("a"), T("b")]);
        await Until(() => cache.LocalPath(Id("a")) is not null && cache.LocalPath(Id("b")) is not null);
        Assert.Equal([Id("a"), Id("b")], _lib.GetTracks([Id("a"), Id("b")]).Select(t => t.VideoId)); // listed offline
        File.SetLastAccessTimeUtc(cache.LocalPath(Id("a"))!, DateTime.UtcNow.AddDays(-1)); // "a" played longest ago

        cache.Want([T("c")]);
        await Until(() => cache.LocalPath(Id("c")) is not null);
        Assert.Null(cache.LocalPath(Id("a")));
        Assert.NotNull(cache.LocalPath(Id("b")));

        // the offline list: downloads first, then the cache
        using var dm = new DownloadManager(_lib, new Resolver(size), new HttpClient(server), _dir + "-dl", new FakeNetwork(), new FakeClock(DateTimeOffset.UnixEpoch));
        var offline = new OfflineLibrary(dm, cache, _lib);
        Assert.Equal(new HashSet<string> { Id("b"), Id("c") }, offline.PlayableTracks().Select(t => t.VideoId).ToHashSet());
        Assert.NotNull(offline.LocalPath(Id("c")));
    }

    [Fact]
    public async Task Downloading_a_cached_song_copies_it_instead_of_fetching_again()
    {
        const int size = 1000;
        var server = new Server(size);
        using var cache = new AudioCache(_lib, new Resolver(size), new HttpClient(server), _dir, new FakeNetwork(), _ => false);
        cache.Start();
        cache.Want([T("a")]);
        await Until(() => cache.LocalPath(Id("a")) is not null);
        var before = server.Requests;

        using var dm = new DownloadManager(_lib, new Resolver(size), new HttpClient(server), _dir + "-dl", new FakeNetwork(), new FakeClock(DateTimeOffset.UnixEpoch))
            { FromCache = cache.LocalPath };
        dm.Start();
        dm.Enqueue([T("a")]);
        await Until(() => dm.IsDownloaded(Id("a")));
        Assert.Equal(before, server.Requests);
        Assert.Equal(size, new FileInfo(dm.LocalPath(Id("a"))!).Length);
        try { Directory.Delete(_dir + "-dl", true); } catch (IOException) { }
    }

    [Fact]
    public void Switched_off_it_saves_nothing()
    {
        _lib.SetSetting("cache.enabled", "0");
        using var cache = new AudioCache(_lib, new Resolver(10), new HttpClient(new Server(10)), _dir, new FakeNetwork(), _ => false);
        cache.Start();
        cache.Want([T("a")]);
        Thread.Sleep(200);
        Assert.Null(cache.LocalPath(Id("a")));
    }
}
