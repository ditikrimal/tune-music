using System.Net;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Offline;
using Tune.Core.Playback;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

public sealed class FakeNetwork : IConnectivity
{
    public bool IsOnline { get; private set; } = true;
    public event Action<bool>? Changed;
    public int Checks { get; private set; }

    public Task<bool> CheckNowAsync()
    {
        Checks++;
        return Task.FromResult(IsOnline);
    }

    public void Set(bool online)
    {
        if (IsOnline == online) return;
        IsOnline = online;
        Changed?.Invoke(online);
    }
}

public sealed class FakeOffline : IOfflineSource
{
    public Dictionary<string, string> Files { get; } = [];
    public List<Track> Downloads { get; } = [];
    public FakeNetwork Net { get; } = new();
    public IConnectivity Network => Net;
    public string? LocalPath(string videoId) => Files.GetValueOrDefault(videoId);
    public IReadOnlyList<Track> PlayableTracks() => Downloads;

    public void Add(Track t)
    {
        Files[t.VideoId] = $@"C:\dl\{t.VideoId}.webm";
        Downloads.Add(t);
    }
}

public class OfflinePlaybackTests : IDisposable
{
    private readonly LibraryStore _lib = new(":memory:");
    private readonly FakePlayer _player = new();
    private readonly FakeResolver _resolver = new();
    private readonly FakeOffline _offline = new();
    private readonly List<string> _notices = [];
    private int _updates;
    private readonly PlaybackService _svc;

    public OfflinePlaybackTests()
    {
        _svc = new PlaybackService(new PlayQueue(), _resolver, _player, _lib, new FakeSponsorBlock(), new FakeYtMusic(),
            _ => { _updates++; return Task.FromResult(true); }, new FakeClock(DateTimeOffset.UnixEpoch), _offline);
        _svc.Notice += _notices.Add;
    }

    public void Dispose() => _lib.Dispose();

    private static Track[] List(params string[] ids) => ids.Select(i => T(i)).ToArray();
    private static string Id(string s) => T(s).VideoId;

    [Fact]
    public async Task Downloaded_song_plays_from_its_file_even_online()
    {
        _offline.Add(T("a"));
        await _svc.PlayListAsync(List("a"), 0);
        Assert.Empty(_resolver.Resolved);
        Assert.True(_player.Loads.Single().Url.IsFile);
        Assert.True(_svc.IsPlaying);
    }

    [Fact]
    public async Task Offline_next_skips_songs_that_are_not_downloaded()
    {
        _offline.Add(T("a"));
        _offline.Add(T("c"));
        await _svc.PlayListAsync(List("a", "b", "c"), 0);
        _offline.Net.Set(false);
        _player.End();
        await _svc.Background;
        Assert.Equal(Id("c"), _svc.Current!.VideoId);
        Assert.Empty(_resolver.Resolved);
    }

    [Fact]
    public async Task Offline_with_nothing_downloaded_in_the_queue_plays_the_downloads()
    {
        _offline.Add(T("x"));
        await _svc.PlayListAsync(List("a", "b"), 0);    // streams a
        _offline.Net.Set(false);
        _player.Fail("network error");                   // a's stream dies
        await _svc.Background;
        Assert.Equal(Id("x"), _svc.Current!.VideoId);
        Assert.True(_svc.IsPlaying);
        Assert.Contains(_notices, n => n.Contains("playing your downloads"));
        Assert.Equal(0, _updates); // no pointless yt-dlp update
    }

    [Fact]
    public async Task Offline_click_on_a_song_that_is_not_downloaded_starts_the_next_downloaded_one()
    {
        _offline.Net.Set(false);
        _offline.Add(T("c"));
        await _svc.PlayListAsync(List("a", "b", "c"), 0);
        Assert.Equal(Id("c"), _svc.Current!.VideoId);
        Assert.True(_svc.IsPlaying);
    }

    [Fact]
    public async Task Offline_click_with_nothing_downloaded_explains_and_keeps_the_current_song()
    {
        _offline.Add(T("a"));
        await _svc.PlayListAsync(List("a"), 0);
        _offline.Net.Set(false);
        await _svc.PlayListAsync(List("z"), 0);
        Assert.Equal(Id("a"), _svc.Current!.VideoId);
        Assert.Contains(_notices, n => n.Contains("download"));
    }

    [Fact]
    public async Task Connection_drop_while_streaming_a_downloaded_song_switches_to_the_file_at_the_same_spot()
    {
        await _svc.PlayListAsync(List("a", "b"), 0);    // streamed
        _offline.Add(T("a"));                             // finished downloading meanwhile
        _player.At(42);
        _offline.Net.Set(false);
        await _svc.Background;
        var last = _player.Loads.Last();
        Assert.True(last.Url.IsFile);
        Assert.Equal(TimeSpan.FromSeconds(42), last.StartAt);
        Assert.Equal(Id("a"), _svc.Current!.VideoId);
    }

    [Fact]
    public async Task Offline_radio_request_just_plays_the_song_if_downloaded()
    {
        _offline.Net.Set(false);
        _offline.Add(T("a"));
        await _svc.PlayRadioAsync(T("a"));
        await _svc.Background;
        Assert.Equal([Id("a")], _svc.Queue.Items.Select(t => t.VideoId));
        Assert.True(_svc.IsPlaying);
    }
}

public class DownloadManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tune-dl-" + Guid.NewGuid().ToString("N"));
    private readonly LibraryStore _lib = new(":memory:");

    public void Dispose()
    {
        _lib.Dispose();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void Reads_size_and_type_from_googlevideo_links()
    {
        var u = new Uri("https://rr1.googlevideo.com/videoplayback?itag=251&mime=audio%2Fwebm&clen=3456789&dur=200");
        Assert.Equal(3456789, DownloadManager.ContentLength(u));
        Assert.Equal(".webm", DownloadManager.ExtensionFor(u));
        Assert.Null(DownloadManager.ContentLength(new Uri("https://x/y")));
    }

    /// <summary>Serves a fake 10 MB file honouring Range requests.</summary>
    private sealed class RangeServer(byte[] data) : HttpMessageHandler
    {
        public List<string> Ranges { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var r = request.Headers.Range!.Ranges.Single();
            Ranges.Add($"{r.From}-{r.To}");
            var from = (int)r.From!.Value;
            var to = (int)Math.Min(r.To!.Value, data.Length - 1);
            var resp = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(data[from..(to + 1)]) };
            resp.Content.Headers.ContentRange = new System.Net.Http.Headers.ContentRangeHeaderValue(from, to, data.Length);
            return Task.FromResult(resp);
        }
    }

    private sealed class UrlResolver(int size) : Tune.Core.Streams.IStreamResolver
    {
        public Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default) =>
            Task.FromResult(new Uri($"https://rr.googlevideo.com/videoplayback?id={videoId}&mime=audio%2Fwebm&clen={size}"));
        public Task PrepareAsync(string videoId, CancellationToken ct = default) => Task.CompletedTask;
        public bool IsReady(string videoId) => true;
        public bool IsKnown(string videoId) => true;
        public void Invalidate(string videoId) { }
        public bool ReportBroken(string videoId) => false;
    }

    [Fact]
    public async Task Downloads_in_ranged_chunks_and_survives_a_restart()
    {
        var data = new byte[10 * 1024 * 1024 + 123];
        Random.Shared.NextBytes(data);
        var server = new RangeServer(data);
        var net = new FakeNetwork();
        var done = new TaskCompletionSource();
        using (var dm = new DownloadManager(_lib, new UrlResolver(data.Length), new HttpClient(server), _dir, net, new FakeClock(DateTimeOffset.UnixEpoch)))
        {
            dm.Changed += id =>
            {
                if (dm.StateOf(id) == DownloadState.Done) done.TrySetResult();
            };
            dm.Start();
            dm.Enqueue([T("a")]);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var path = dm.LocalPath(T("a").VideoId)!;
            Assert.EndsWith(".webm", path);
            Assert.Equal(data, File.ReadAllBytes(path));
            Assert.Equal(3, server.Ranges.Count); // 4 + 4 + 2 MB
            Assert.Single(dm.DownloadedTracks());
        }

        using var again = new DownloadManager(_lib, new UrlResolver(data.Length), new HttpClient(server), _dir, net, new FakeClock(DateTimeOffset.UnixEpoch));
        again.Start();
        Assert.Equal(DownloadState.Done, again.StateOf(T("a").VideoId));

        again.Remove([T("a").VideoId]);
        Assert.Equal(DownloadState.None, again.StateOf(T("a").VideoId));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Empty(_lib.GetDownloads());
    }

    [Fact]
    public async Task Waits_while_offline_and_downloads_when_back()
    {
        var data = new byte[1000];
        var net = new FakeNetwork();
        net.Set(false);
        var done = new TaskCompletionSource();
        using var dm = new DownloadManager(_lib, new UrlResolver(data.Length), new HttpClient(new RangeServer(data)), _dir, net, new FakeClock(DateTimeOffset.UnixEpoch));
        dm.Changed += id =>
        {
            if (dm.StateOf(id) == DownloadState.Done) done.TrySetResult();
        };
        dm.Start();
        dm.Enqueue([T("a")]);
        await Task.Delay(200);
        Assert.Equal(DownloadState.Queued, dm.StateOf(T("a").VideoId));
        net.Set(true);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }
}
