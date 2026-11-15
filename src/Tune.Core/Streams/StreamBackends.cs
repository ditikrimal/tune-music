using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tune.Core.Util;

namespace Tune.Core.Streams;

/// <summary>Raw stream lookup for one video: the audio URL plus YouTube's "available at" moment (0 = now).</summary>
/// <remarks><paramref name="Source"/> says which path produced the link ("worker" or "exe").</remarks>
public sealed record ExtractedStream(Uri Url, long AvailableAtUnix, string Source = "exe");

/// <summary>Something that can ask YouTube for a song's audio URL (yt-dlp in one form or another).</summary>
public interface IStreamBackend
{
    Task<ExtractedStream> ExtractAsync(string videoId, CancellationToken ct);
}

/// <summary>Runs the standalone yt-dlp.exe once per song (slow start, no setup needed).</summary>
public sealed class YtDlpExeBackend(ToolManager tools, IProcessRunner runner) : IStreamBackend
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    public async Task<ExtractedStream> ExtractAsync(string videoId, CancellationToken ct)
    {
        string[] args =
        [
            "-f", "bestaudio", "--no-playlist", "--no-warnings", "--no-progress", "--ignore-config",
            "--js-runtimes", "deno:" + tools.DenoPath,
            "--print", "%(available_at|0)s", "--print", "%(url)s",
            "https://music.youtube.com/watch?v=" + videoId,
        ];
        var (exit, stdout, stderr) = await runner.RunAsync(tools.YtDlpPath, args, Timeout, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new StreamResolveException(videoId, LastError(stderr) ?? $"yt-dlp exited with {exit}");

        var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var line = lines.FirstOrDefault(l => l.StartsWith("https://", StringComparison.Ordinal));
        if (line is null || !Uri.TryCreate(line, UriKind.Absolute, out var url))
            throw new StreamResolveException(videoId, "yt-dlp returned no stream URL");
        var availableAt = lines.Select(l => long.TryParse(l, out var t) ? t : 0).FirstOrDefault(t => t > 0);
        return new ExtractedStream(url, availableAt);
    }

    private static string? LastError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal)) ?? lines.LastOrDefault();
    }
}

/// <summary>
/// The JSON-lines conversation with tune_worker.py, separated from process handling so it can be tested.
/// Requests can be answered in any order; each waits for the reply with its own id.
/// </summary>
public sealed class WorkerProtocol
{
    private readonly TextWriter _toWorker;
    private readonly ConcurrentDictionary<long, (string VideoId, TaskCompletionSource<ExtractedStream> Reply)> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private long _nextId;

    public WorkerProtocol(TextWriter toWorker, TextReader fromWorker)
    {
        _toWorker = toWorker;
        Completion = Task.Run(() => ReadLoopAsync(fromWorker));
    }

    /// <summary>Completes when the worker's output ends (process exited); all pending requests fail.</summary>
    public Task Completion { get; }

    /// <summary>Set when the worker reports it has loaded yt-dlp.</summary>
    public TaskCompletionSource<string> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsAlive => !Completion.IsCompleted;

    public async Task<ExtractedStream> ExtractAsync(string videoId, CancellationToken ct)
    {
        if (!IsAlive) throw new StreamResolveException(videoId, "resolver worker has stopped");
        var id = Interlocked.Increment(ref _nextId);
        var reply = new TaskCompletionSource<ExtractedStream>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (videoId, reply);
        try
        {
            var line = new JsonObject { ["id"] = id, ["v"] = videoId }.ToJsonString();
            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _toWorker.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
                await _toWorker.FlushAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
            // the worker can't abandon a running lookup; a cancelled caller just stops waiting
            return await reply.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            throw new StreamResolveException(videoId, "resolver worker has stopped: " + e.Message);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private async Task ReadLoopAsync(TextReader fromWorker)
    {
        try
        {
            while (await fromWorker.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (JsonException) { continue; }
                if (msg is null) continue;

                if (msg["ready"] is not null)
                {
                    Ready.TrySetResult((string?)msg["version"] ?? "?");
                    continue;
                }
                if (msg["id"] is not JsonValue idNode || !idNode.TryGetValue<long>(out var id)) continue;
                if (!_pending.TryGetValue(id, out var p)) continue;

                if (msg["ok"]?.GetValue<bool>() == true
                    && Uri.TryCreate((string?)msg["url"], UriKind.Absolute, out var url))
                {
                    var at = msg["available_at"] is JsonValue a && a.TryGetValue<double>(out var d) ? (long)d : 0;
                    p.Reply.TrySetResult(new ExtractedStream(url, at, "worker"));
                }
                else
                {
                    p.Reply.TrySetException(new StreamResolveException(p.VideoId, (string?)msg["error"] ?? "unknown error"));
                }
            }
        }
        catch (IOException)
        {
            // pipe closed
        }
        finally
        {
            Ready.TrySetException(new StreamResolveException("", "resolver worker exited"));
            foreach (var (_, p) in _pending)
                p.Reply.TrySetException(new StreamResolveException(p.VideoId, "resolver worker exited"));
        }
    }
}

/// <summary>
/// Keeps one Python process with yt-dlp loaded (tune_worker.py) and sends it lookups; restarts it if it dies.
/// Saves the per-song yt-dlp.exe start-up and lets several songs resolve in parallel.
/// </summary>
public sealed class PythonWorkerBackend : IStreamBackend, IDisposable
{
    private readonly string _python;
    private readonly string? _pythonHome;
    private readonly string _pyLib;
    private readonly string _denoPath;
    private readonly string _scriptPath;
    private readonly int _threads;
    private readonly Lock _gate = new();
    private Process? _process;
    private WorkerProtocol? _protocol;

    /// <param name="pythonHome">Set when <paramref name="python"/> is a relocated copy of the interpreter.</param>
    public PythonWorkerBackend(string python, string pyLib, string denoPath, string workDir, int threads = 3, string? pythonHome = null)
    {
        _python = python;
        _pythonHome = pythonHome;
        _pyLib = pyLib;
        _denoPath = denoPath;
        _threads = threads;
        Directory.CreateDirectory(workDir);
        _scriptPath = Path.Combine(workDir, "tune_worker.py");
        using var src = typeof(PythonWorkerBackend).Assembly.GetManifestResourceStream("tune_worker.py")
                        ?? throw new InvalidOperationException("tune_worker.py not embedded");
        using var dst = File.Create(_scriptPath);
        src.CopyTo(dst);
    }

    /// <summary>Starts the worker and waits until yt-dlp is imported. Returns the yt-dlp version.</summary>
    public async Task<string> StartAsync(CancellationToken ct = default) =>
        await EnsureRunning().Ready.Task.WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);

    public Task<ExtractedStream> ExtractAsync(string videoId, CancellationToken ct) =>
        EnsureRunning().ExtractAsync(videoId, ct);

    private WorkerProtocol EnsureRunning()
    {
        lock (_gate)
        {
            if (_protocol is { IsAlive: true }) return _protocol;
            try { _process?.Kill(); } catch (InvalidOperationException) { }
            _process?.Dispose();

            var psi = new ProcessStartInfo(_python)
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            // -s: ignore the user's site-packages. With a relocated interpreter PYTHONHOME finds the stdlib;
            // -I would ignore PYTHONHOME, so isolation is done by hand (no user site, no PYTHONPATH).
            foreach (var a in new[] { "-s", "-u", _scriptPath, _pyLib, _denoPath, _threads.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                psi.ArgumentList.Add(a);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONNOUSERSITE"] = "1";
            psi.Environment.Remove("PYTHONPATH");
            psi.Environment.Remove("PYTHONSTARTUP");
            if (_pythonHome is not null) psi.Environment["PYTHONHOME"] = _pythonHome;
            else psi.Environment.Remove("PYTHONHOME");

            _process = Process.Start(psi) ?? throw new InvalidOperationException("could not start Python");
            KillOnExitJob.Add(_process);
            _process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) Log.Warn("worker: " + e.Data); };
            _process.BeginErrorReadLine();
            _protocol = new WorkerProtocol(_process.StandardInput, _process.StandardOutput);
            Log.Info($"resolver worker started (pid {_process.Id})");
            return _protocol;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            try { _process?.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _process?.Dispose();
            _process = null;
        }
    }
}

/// <summary>Told when a link a backend produced turned out not to play.</summary>
public interface IBrokenLinkSink
{
    /// <summary>Returns true when a different path will be used for the next lookup.</summary>
    bool LinkBroken(string source);
}

/// <summary>
/// Uses the fast backend when it's available, otherwise (or when it breaks) the fallback.
/// If a fast-path link fails to play (e.g. a VPN rule changed its route), the fast path is benched for 30 min.
/// </summary>
public sealed class FallbackBackend(Func<IStreamBackend?> primary, IStreamBackend fallback, Tune.Core.Playback.IClock? clock = null)
    : IStreamBackend, IBrokenLinkSink
{
    private static readonly TimeSpan Bench = TimeSpan.FromMinutes(30);
    private readonly Tune.Core.Playback.IClock _clock = clock ?? new Tune.Core.Playback.SystemClock();
    private DateTimeOffset _benchedUntil = DateTimeOffset.MinValue;

    public bool LinkBroken(string source)
    {
        if (source != "worker") return false;
        _benchedUntil = _clock.Now + Bench;
        Log.Warn("A fast-resolver link didn't play; using yt-dlp.exe for 30 minutes");
        return true;
    }

    public async Task<ExtractedStream> ExtractAsync(string videoId, CancellationToken ct)
    {
        if (_clock.Now >= _benchedUntil && primary() is { } fast)
        {
            try
            {
                return await fast.ExtractAsync(videoId, ct).ConfigureAwait(false);
            }
            catch (StreamResolveException e) when (e.Message.StartsWith("resolver worker", StringComparison.Ordinal))
            {
                Log.Warn("Fast resolver unavailable, using yt-dlp.exe", e);
            }
        }
        return await fallback.ExtractAsync(videoId, ct).ConfigureAwait(false);
    }
}
