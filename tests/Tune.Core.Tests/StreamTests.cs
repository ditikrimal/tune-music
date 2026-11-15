using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Tune.Core.Library;
using Tune.Core.Streams;

namespace Tune.Core.Tests;

public sealed class FakeRunner : IProcessRunner
{
    public List<(string Exe, IReadOnlyList<string> Args)> Calls { get; } = [];
    public Func<IReadOnlyList<string>, (int, string, string)> Respond { get; set; } = _ => (0, "", "");

    /// <summary>When set, runs block until the gate completes (or the run is cancelled, like a killed process).</summary>
    public TaskCompletionSource? Gate { get; set; }
    public List<CancellationToken> Tokens { get; } = [];

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add((exe, args));
        Tokens.Add(ct);
        if (Gate is { } g) await g.Task.WaitAsync(ct);
        return Respond(args);
    }
}

public sealed class BytesHandler(Func<Uri, byte[]> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(respond(request.RequestUri!)) });
    }
}

public class StreamTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("tune-tools").FullName;
    private readonly LibraryStore _lib = new(":memory:");
    private readonly FakeClock _clock = new(Now);
    private readonly FakeRunner _runner = new();

    public void Dispose()
    {
        _lib.Dispose();
        Directory.Delete(_dir, true);
    }

    // npm tarball layout: gzip(tar) with package/deno.exe
    private static byte[] DenoTgz()
    {
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
        using (var tar = new TarWriter(gz))
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile, "package/deno.exe") { DataStream = new MemoryStream("fake deno"u8.ToArray()) };
            tar.WriteEntry(entry);
        }
        return ms.ToArray();
    }

    private static string Integrity(byte[] data) => "sha512-" + Convert.ToBase64String(SHA512.HashData(data));

    private ToolManager Tools(BytesHandler? handler = null, ToolManifest? manifest = null) =>
        new(_dir, new HttpClient(handler ?? new BytesHandler(_ => [])), _runner, _lib, _clock, manifest ?? ToolManifest.Load());

    private static string Url(DateTimeOffset expire) =>
        $"https://rr1.googlevideo.com/videoplayback?expire={expire.ToUnixTimeSeconds()}&id=1";

    [Fact]
    public void Manifest_is_embedded_and_pins_deno()
    {
        var m = ToolManifest.Load();
        Assert.StartsWith("sha512-", m.DenoIntegrity);
        Assert.StartsWith("https://registry.npmjs.org/@deno/win32-x64/", m.DenoUrl);
    }

    [Fact]
    public async Task Resolve_passes_expected_args()
    {
        _runner.Respond = _ => (0, Url(Now.AddHours(6)) + "\n", "");
        var tools = Tools();
        var url = await new StreamResolver(tools, _runner, _clock).ResolveAsync("oyr9_tu_F2U");
        var (exe, args) = _runner.Calls.Single();
        Assert.Equal(tools.YtDlpPath, exe);
        Assert.Equal(["-f", "bestaudio", "--no-playlist", "--no-warnings", "--no-progress",
            "--js-runtimes", "deno:" + tools.DenoPath, "--print", "%(available_at|0)s", "--print", "%(url)s",
            "https://music.youtube.com/watch?v=oyr9_tu_F2U"], args);
        Assert.Equal("rr1.googlevideo.com", url.Host);
    }

    [Fact]
    public async Task Resolve_waits_until_stream_is_available()
    {
        // YouTube locks the stream for the length of the skipped pre-roll ad (403 until available_at)
        var waits = new List<TimeSpan>();
        _runner.Respond = _ => (0, $"{Now.AddSeconds(4).ToUnixTimeSeconds()}\n{Url(Now.AddHours(6))}\n", "");
        var r = new StreamResolver(Tools(), _runner, _clock, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        await r.ResolveAsync("abcdefghijk");
        Assert.Equal([TimeSpan.FromSeconds(4.5)], waits); // + 0.5 s margin
        _clock.Now = Now.AddSeconds(10);
        await r.ResolveAsync("abcdefghijk"); // cached and already available: no wait
        Assert.Single(waits);
    }

    [Fact]
    public async Task Resolve_does_not_wait_when_available_at_is_missing_or_past()
    {
        var waits = new List<TimeSpan>();
        var r = new StreamResolver(Tools(), _runner, _clock, (d, _) => { waits.Add(d); return Task.CompletedTask; });
        _runner.Respond = _ => (0, $"0\n{Url(Now.AddHours(6))}\n", "");
        await r.ResolveAsync("aaaaaaaaaaa");
        _runner.Respond = _ => (0, $"{Now.AddSeconds(-3).ToUnixTimeSeconds()}\n{Url(Now.AddHours(6))}\n", "");
        await r.ResolveAsync("bbbbbbbbbbb");
        _runner.Respond = _ => (0, $"NA\n{Url(Now.AddHours(6))}\n", "");
        await r.ResolveAsync("ccccccccccc");
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Resolve_caches_until_expire_minus_5min()
    {
        _runner.Respond = _ => (0, Url(Now.AddHours(1)), "");
        var r = new StreamResolver(Tools(), _runner, _clock);
        await r.ResolveAsync("abcdefghijk");
        _clock.Now = Now.AddMinutes(54);
        await r.ResolveAsync("abcdefghijk");
        Assert.Single(_runner.Calls);
        _clock.Now = Now.AddMinutes(56);
        await r.ResolveAsync("abcdefghijk");
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task Invalidate_forces_reresolve()
    {
        _runner.Respond = _ => (0, Url(Now.AddHours(6)), "");
        var r = new StreamResolver(Tools(), _runner, _clock);
        await r.ResolveAsync("abcdefghijk");
        r.Invalidate("abcdefghijk");
        await r.ResolveAsync("abcdefghijk");
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task Concurrent_resolves_of_one_song_share_one_yt_dlp_run()
    {
        _runner.Gate = new TaskCompletionSource();
        _runner.Respond = _ => (0, Url(Now.AddHours(6)), "");
        var r = new StreamResolver(Tools(), _runner, _clock);
        var a = r.ResolveAsync("abcdefghijk");   // e.g. look-ahead
        var b = r.ResolveAsync("abcdefghijk");   // user pressed Next meanwhile
        _runner.Gate.SetResult();
        Assert.Equal((await a).ToString(), (await b).ToString());
        Assert.Single(_runner.Calls);
    }

    [Fact]
    public async Task Run_is_killed_only_when_every_waiter_gave_up()
    {
        _runner.Gate = new TaskCompletionSource();
        _runner.Respond = _ => (0, Url(Now.AddHours(6)), "");
        var r = new StreamResolver(Tools(), _runner, _clock);
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = r.ResolveAsync("abcdefghijk", first.Token);
        var b = r.ResolveAsync("abcdefghijk", second.Token);
        first.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.False(_runner.Tokens.Single().IsCancellationRequested); // b still needs it
        second.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => b);
        Assert.True(_runner.Tokens.Single().IsCancellationRequested);  // nobody left: yt-dlp killed
        // a later request starts a fresh run
        _runner.Gate = null;
        await r.ResolveAsync("abcdefghijk");
        Assert.Equal(2, _runner.Calls.Count);
    }

    [Fact]
    public async Task Nonzero_exit_throws_StreamResolveException_with_stderr()
    {
        _runner.Respond = _ => (1, "", "WARNING: x\nERROR: [youtube] abcdefghijk: Video unavailable\n");
        var r = new StreamResolver(Tools(), _runner, _clock);
        var e = await Assert.ThrowsAsync<StreamResolveException>(() => r.ResolveAsync("abcdefghijk"));
        Assert.Equal("abcdefghijk", e.VideoId);
        Assert.Contains("Video unavailable", e.Message);
    }

    [Fact]
    public async Task Garbage_stdout_throws_StreamResolveException()
    {
        _runner.Respond = _ => (0, "not a url", "");
        var r = new StreamResolver(Tools(), _runner, _clock);
        await Assert.ThrowsAsync<StreamResolveException>(() => r.ResolveAsync("abcdefghijk"));
    }

    [Fact]
    public async Task Update_runs_at_most_once_per_24h_unless_forced()
    {
        var tools = Tools();
        Assert.True(await tools.UpdateYtDlpIfDueAsync(force: false));
        _clock.Now = Now.AddHours(23);
        Assert.False(await tools.UpdateYtDlpIfDueAsync(force: false));
        Assert.True(await tools.UpdateYtDlpIfDueAsync(force: true));
        Assert.Equal(2, _runner.Calls.Count);
        Assert.All(_runner.Calls, c => Assert.Equal(["-U"], c.Args));
    }

    [Fact]
    public async Task EnsureInstalled_downloads_missing_tools_and_verifies_deno()
    {
        var tgz = DenoTgz();
        var manifest = ToolManifest.Load() with { DenoIntegrity = Integrity(tgz) };
        var handler = new BytesHandler(u => u.AbsolutePath.EndsWith(".tgz") ? tgz : "fake yt-dlp"u8.ToArray());
        var tools = Tools(handler, manifest);
        await tools.EnsureInstalledAsync();
        Assert.Equal("fake deno", File.ReadAllText(tools.DenoPath));
        Assert.Equal("fake yt-dlp", File.ReadAllText(tools.YtDlpPath));
        await tools.EnsureInstalledAsync(); // already present: no new downloads
        Assert.Equal(2, handler.Requests.Count);
    }

    private sealed class SyncProgress(List<ToolProgress> sink) : IProgress<ToolProgress>
    {
        public void Report(ToolProgress value) { lock (sink) sink.Add(value); }
    }

    [Fact]
    public async Task EnsureInstalled_reports_progress_per_tool()
    {
        var tgz = DenoTgz();
        var manifest = ToolManifest.Load() with { DenoIntegrity = Integrity(tgz) };
        var handler = new BytesHandler(u => u.AbsolutePath.EndsWith(".tgz") ? tgz : new byte[200_000]);
        var reports = new List<ToolProgress>();
        await Tools(handler, manifest).EnsureInstalledAsync(new SyncProgress(reports));
        Assert.Contains(reports, r => r.Tool == "yt-dlp" && r.Fraction is 1.0);
        Assert.Contains(reports, r => r.Tool == "deno" && r.Fraction is 1.0);
        Assert.All(reports, r => Assert.InRange(r.Fraction ?? 0, 0, 1));
    }

    [Fact]
    [Trait("Category", "Live")]
    public async Task Live_install_and_resolve_real_song()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tune-live-tools"); // reused across runs to avoid re-downloading
        var tools = new ToolManager(dir, new HttpClient(), new ProcessRunner(), _lib, new Tune.Core.Playback.SystemClock(), ToolManifest.Load());
        await tools.EnsureInstalledAsync();
        var url = await new StreamResolver(tools, new ProcessRunner(), new Tune.Core.Playback.SystemClock()).ResolveAsync("oyr9_tu_F2U");
        Assert.EndsWith("googlevideo.com", url.Host);
    }

    [Fact]
    public async Task Deno_sha_mismatch_throws_and_deletes_file()
    {
        var handler = new BytesHandler(u => u.AbsolutePath.EndsWith(".tgz") ? DenoTgz() : "y"u8.ToArray());
        var tools = Tools(handler, ToolManifest.Load() with { DenoIntegrity = Integrity("other"u8.ToArray()) });
        await Assert.ThrowsAsync<ToolInstallException>(() => tools.EnsureInstalledAsync());
        Assert.False(File.Exists(tools.DenoPath));
        Assert.Empty(Directory.GetFiles(_dir, "*.tgz"));
    }
}
