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

    public Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        Calls.Add((exe, args));
        return Task.FromResult(Respond(args));
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
        Assert.Equal(["-f", "bestaudio", "-g", "--no-playlist", "--no-warnings", "--no-progress",
            "--js-runtimes", "deno:" + tools.DenoPath, "https://music.youtube.com/watch?v=oyr9_tu_F2U"], args);
        Assert.Equal("rr1.googlevideo.com", url.Host);
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
