using System.IO.Pipes;
using Tune.Core.Streams;

namespace Tune.Core.Tests;

public sealed class FakeBackend : IStreamBackend
{
    public List<string> Calls { get; } = [];
    public Dictionary<string, TaskCompletionSource> Gates { get; } = [];
    public Func<string, ExtractedStream> Answer { get; set; } = id =>
        new ExtractedStream(new Uri($"https://rr.googlevideo.com/{id}?expire=1894760000"), 0);

    public async Task<ExtractedStream> ExtractAsync(string videoId, CancellationToken ct)
    {
        lock (Calls) Calls.Add(videoId);
        if (Gates.TryGetValue(videoId, out var g)) await g.Task.WaitAsync(ct);
        return Answer(videoId);
    }
}

public sealed class MemoryStreamCache : IStreamCacheStore
{
    public List<CachedStream> Saved { get; } = [];
    public List<CachedStream> Initial { get; } = [];
    public IReadOnlyList<CachedStream> LoadStreams(DateTimeOffset now) => Initial.Where(s => s.ValidUntil > now).ToList();
    public void SaveStream(CachedStream s) => Saved.Add(s);
}

public class ResolverPrepareTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly FakeClock _clock = new(Now);
    private readonly FakeBackend _backend = new();
    private readonly List<TimeSpan> _waits = [];

    private StreamResolver Make(IStreamCacheStore? store = null) =>
        new(_backend, _clock, (d, _) => { _waits.Add(d); return Task.CompletedTask; }, store);

    [Fact]
    public async Task Prepare_does_not_wait_for_youtubes_start_delay()
    {
        _backend.Answer = id => new ExtractedStream(new Uri("https://rr.googlevideo.com/x?expire=1894760000"), Now.AddSeconds(15).ToUnixTimeSeconds());
        var r = Make();
        await r.PrepareAsync("aaaaaaaaaaa");
        Assert.Empty(_waits);
        Assert.False(r.IsReady("aaaaaaaaaaa"));      // still locked by YouTube
        _clock.Now = Now.AddSeconds(16);
        Assert.True(r.IsReady("aaaaaaaaaaa"));
        await r.ResolveAsync("aaaaaaaaaaa");          // instant: cached and unlocked
        Assert.Empty(_waits);
        Assert.Single(_backend.Calls);
    }

    [Fact]
    public async Task Unknown_song_is_not_ready()
    {
        var r = Make();
        Assert.False(r.IsReady("bbbbbbbbbbb"));
        await r.PrepareAsync("bbbbbbbbbbb");
        Assert.True(r.IsReady("bbbbbbbbbbb"));
    }

    [Fact]
    public async Task Prepared_links_are_saved_and_restored_after_restart()
    {
        var store = new MemoryStreamCache();
        await Make(store).PrepareAsync("ccccccccccc");
        var saved = Assert.Single(store.Saved);
        store.Initial.Add(saved);
        store.Initial.Add(saved with { VideoId = "ddddddddddd", ValidUntil = Now.AddMinutes(-1) }); // expired
        var restarted = Make(store);
        Assert.True(restarted.IsReady("ccccccccccc"));
        Assert.False(restarted.IsReady("ddddddddddd"));
        await restarted.ResolveAsync("ccccccccccc");
        Assert.Single(_backend.Calls);                // no new lookup after the restart
    }
}

public class PythonWorkerLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task Real_worker_resolves_songs_in_parallel()
    {
        var python = ToolManager.FindPython(Environment.GetEnvironmentVariable("PATH"), File.Exists)
                     ?? throw new InvalidOperationException("python not on PATH");
        var data = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Tune");
        using var worker = new PythonWorkerBackend(python, Path.Combine(data, "tools", "pylib"),
            Path.Combine(data, "tools", "deno.exe"), Path.Combine(Path.GetTempPath(), "tune-worker-test"));
        var version = await worker.StartAsync();
        Assert.False(string.IsNullOrEmpty(version));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = await Task.WhenAll(
            worker.ExtractAsync("oyr9_tu_F2U", CancellationToken.None),
            worker.ExtractAsync("lp-EO5I60KA", CancellationToken.None),
            worker.ExtractAsync("CKJA9blyMUg", CancellationToken.None));
        Assert.All(results, r => Assert.EndsWith("googlevideo.com", r.Url.Host));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15), $"3 parallel lookups took {sw.Elapsed}");
    }
}

public class SwitchSpeedLiveTests
{
    [Fact]
    [Trait("Category", "Live")]
    public async Task Prepared_song_starts_instantly_cold_song_does_not()
    {
        var data = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Tune");
        var python = ToolManager.FindPython(Environment.GetEnvironmentVariable("PATH"), File.Exists)!;
        using var lib = new Tune.Core.Library.LibraryStore(":memory:");
        var clock = new Tune.Core.Playback.SystemClock();
        var tools = new ToolManager(Path.Combine(data, "tools"), new HttpClient(), new ProcessRunner(), lib, clock, ToolManifest.Load());
        var py = tools.PreparePrivatePython(python);
        using var worker = new PythonWorkerBackend(py.Exe, tools.PyLibDir, tools.DenoPath,
            Path.Combine(Path.GetTempPath(), "tune-worker-test"), pythonHome: py.Home);
        await worker.StartAsync();
        var resolver = new StreamResolver(worker, clock);
        var prefetcher = new StreamPrefetcher(resolver);

        prefetcher.Request(["XPDec1vxj6s", "T6jFshH6XmY", "Cu7boz3ODPo"], PrefetchPriority.Page); // "visible on the page"
        await Task.Delay(TimeSpan.FromSeconds(25));                                                 // user looks at the page

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var url = await resolver.ResolveAsync("T6jFshH6XmY");                                      // user clicks #2
        var prepared = sw.Elapsed;

        sw.Restart();
        await resolver.ResolveAsync("OBhN_FwzFEg");                                                 // a song nobody prepared
        var cold = sw.Elapsed;

        // the link must play from this process's network route (what mpv uses)
        using var http = new HttpClient();
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);

        Console.WriteLine($"prepared click: {prepared.TotalMilliseconds:0} ms, cold click: {cold.TotalSeconds:0.0} s, link HTTP {(int)resp.StatusCode}");
        Assert.True(resp.IsSuccessStatusCode, $"link refused: {(int)resp.StatusCode}");
        Assert.True(prepared < TimeSpan.FromMilliseconds(300), $"prepared click took {prepared}");
    }
}

public class WorkerProtocolTests
{
    // two in-process pipes stand in for the worker's stdin/stdout
    private sealed class FakeWorker : IDisposable
    {
        private readonly AnonymousPipeServerStream _requestsOut = new(PipeDirection.Out);
        private readonly AnonymousPipeServerStream _repliesOut = new(PipeDirection.Out);
        public StreamReader Requests { get; }
        public StreamWriter Replies { get; }
        public WorkerProtocol Protocol { get; }

        public FakeWorker()
        {
            var requestsIn = new AnonymousPipeClientStream(PipeDirection.In, _requestsOut.ClientSafePipeHandle);
            var repliesIn = new AnonymousPipeClientStream(PipeDirection.In, _repliesOut.ClientSafePipeHandle);
            Requests = new StreamReader(requestsIn);
            Replies = new StreamWriter(_repliesOut) { AutoFlush = true };
            Protocol = new WorkerProtocol(new StreamWriter(_requestsOut) { AutoFlush = true }, new StreamReader(repliesIn));
        }

        public void Dispose()
        {
            Replies.Dispose();
            _requestsOut.Dispose();
        }
    }

    [Fact]
    public async Task Replies_in_any_order_reach_the_right_caller()
    {
        using var w = new FakeWorker();
        var a = w.Protocol.ExtractAsync("aaaaaaaaaaa", CancellationToken.None);
        var b = w.Protocol.ExtractAsync("bbbbbbbbbbb", CancellationToken.None);
        var reqA = await w.Requests.ReadLineAsync();
        var reqB = await w.Requests.ReadLineAsync();
        Assert.Contains("aaaaaaaaaaa", reqA);
        Assert.Contains("bbbbbbbbbbb", reqB);
        await w.Replies.WriteLineAsync("""{"id":2,"ok":true,"url":"https://x.googlevideo.com/b","available_at":1794760775}""");
        await w.Replies.WriteLineAsync("""{"id":1,"ok":true,"url":"https://x.googlevideo.com/a","available_at":0}""");
        Assert.Equal("/a", (await a).Url.AbsolutePath);
        var rb = await b;
        Assert.Equal("/b", rb.Url.AbsolutePath);
        Assert.Equal(1794760775, rb.AvailableAtUnix);
    }

    [Fact]
    public async Task Worker_error_becomes_StreamResolveException()
    {
        using var w = new FakeWorker();
        var a = w.Protocol.ExtractAsync("aaaaaaaaaaa", CancellationToken.None);
        await w.Requests.ReadLineAsync();
        await w.Replies.WriteLineAsync("""{"id":1,"ok":false,"error":"ERROR: Video unavailable"}""");
        var e = await Assert.ThrowsAsync<StreamResolveException>(() => a);
        Assert.Contains("Video unavailable", e.Message);
    }

    [Fact]
    public async Task Worker_exit_fails_pending_requests_and_reports_stopped()
    {
        var w = new FakeWorker();
        var a = w.Protocol.ExtractAsync("aaaaaaaaaaa", CancellationToken.None);
        await w.Requests.ReadLineAsync();
        w.Dispose();                                  // process died
        var e = await Assert.ThrowsAsync<StreamResolveException>(() => a);
        Assert.StartsWith("resolver worker", e.Message);
        await w.Protocol.Completion;
        Assert.False(w.Protocol.IsAlive);
    }

    [Fact]
    public async Task Ready_line_reports_version()
    {
        using var w = new FakeWorker();
        await w.Replies.WriteLineAsync("""{"ready":true,"version":"2026.10.26"}""");
        Assert.Equal("2026.10.26", await w.Protocol.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
