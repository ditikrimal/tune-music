using System.Collections.Concurrent;
using System.Net;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Streams;
using Tune.Core.Util;

namespace Tune.Core.Offline;

/// <summary>
/// Streaming cache (like Spotify's): songs you play online are also saved here in the background, the current song
/// first and then the next couple in the queue, so they play again without internet. Least recently played goes
/// first when it's over its size. Downloads are separate and never touched.
/// </summary>
public sealed class AudioCache : IDisposable
{
    private const int MaxAttempts = 2;

    private readonly LibraryStore _lib;
    private readonly IStreamResolver _resolver;
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly IConnectivity _network;
    private readonly Func<string, bool> _isDownloaded;
    private readonly ConcurrentDictionary<string, string> _files = new(); // videoId -> audio file
    private readonly Lock _gate = new();
    private List<Track> _wanted = [];
    private readonly Dictionary<string, int> _attempts = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private string? _busy;

    public AudioCache(LibraryStore lib, IStreamResolver resolver, HttpClient http, string dir, IConnectivity network,
        Func<string, bool> isDownloaded)
    {
        _lib = lib;
        _resolver = resolver;
        _http = http;
        _dir = dir;
        _network = network;
        _isDownloaded = isDownloaded;
        _network.Changed += online =>
        {
            if (online) _wake.Release();
        };
    }

    public const long DefaultMaxBytes = 1L << 30;

    public bool Enabled
    {
        get => _lib.GetSetting("cache.enabled") != "0";
        set => _lib.SetSetting("cache.enabled", value ? "1" : "0");
    }

    public long MaxBytes =>
        long.TryParse(_lib.GetSetting("cache.maxBytes"), out var n) && n > 0 ? n : DefaultMaxBytes;

    /// <summary>The cache gained or lost songs (any thread).</summary>
    public event Action? Changed;

    public void Start()
    {
        Directory.CreateDirectory(_dir);
        foreach (var f in Directory.EnumerateFiles(_dir))
        {
            if (f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) TryDelete(f);
            else _files[Path.GetFileNameWithoutExtension(f)] = f;
        }
        _ = Task.Run(WorkAsync);
    }

    public string? LocalPath(string videoId) =>
        _files.TryGetValue(videoId, out var p) && File.Exists(p) ? p : null;

    /// <summary>Cached songs, most recently played first.</summary>
    public IReadOnlyList<string> Ids() =>
        _files.Select(kv => (kv.Key, At: LastUsed(kv.Value))).OrderByDescending(x => x.At).Select(x => x.Key).ToList();

    public long UsedBytes() => _files.Values.Sum(SizeOf);

    /// <summary>The songs to keep: the first is playing now. Replaces what was asked before.</summary>
    public void Want(IReadOnlyList<Track> tracks)
    {
        if (tracks.Count > 0 && LocalPath(tracks[0].VideoId) is { } now) Touch(now); // played again: keep it longer
        if (!Enabled) return;
        lock (_gate)
            _wanted = tracks.Where(t => !_files.ContainsKey(t.VideoId) && !_isDownloaded(t.VideoId)
                && _attempts.GetValueOrDefault(t.VideoId) < MaxAttempts).ToList();
        if (_wanted.Count > 0) _wake.Release();
    }

    public void Clear()
    {
        lock (_gate) _wanted = [];
        foreach (var id in _files.Keys.ToList())
            if (id != _busy && _files.TryRemove(id, out var path)) TryDelete(path);
        Changed?.Invoke();
    }

    private async Task WorkAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Track? next;
            lock (_gate)
            {
                next = _wanted.FirstOrDefault();
                if (next is not null) _wanted.RemoveAt(0);
            }
            if (next is null || !_network.IsOnline || !Enabled)
            {
                try { await _wake.WaitAsync(ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }
            if (_files.ContainsKey(next.VideoId) || _isDownloaded(next.VideoId)) continue;

            _busy = next.VideoId;
            var part = Path.Combine(_dir, next.VideoId + ".part");
            try
            {
                var url = await _resolver.ResolveAsync(next.VideoId, ct).ConfigureAwait(false);
                var bytes = await ChunkedDownload.FetchAsync(_http, url, part, null, ct).ConfigureAwait(false);
                var file = Path.Combine(_dir, next.VideoId + DownloadManager.ExtensionFor(url));
                File.Move(part, file, overwrite: true);
                _lib.SaveTracks([next]); // so it can be listed and played offline
                _files[next.VideoId] = file;
                Touch(file);
                Log.Info($"Cached {next.VideoId} ({bytes / 1024} KB)");
                Trim();
                Changed?.Invoke();
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(part);
                return;
            }
            catch (Exception e)
            {
                TryDelete(part);
                if (e is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Gone }) _resolver.Invalidate(next.VideoId);
                lock (_gate) _attempts[next.VideoId] = _attempts.GetValueOrDefault(next.VideoId) + 1;
                Log.Warn($"Caching {next.VideoId} failed: {e.Message}");
            }
            finally
            {
                _busy = null;
            }
        }
    }

    /// <summary>Drops the least recently played songs until the cache fits its size.</summary>
    private void Trim()
    {
        var max = MaxBytes;
        var all = _files.Select(kv => (Id: kv.Key, Path: kv.Value, Size: SizeOf(kv.Value), At: LastUsed(kv.Value)))
            .OrderBy(x => x.At).ToList();
        var used = all.Sum(x => x.Size);
        foreach (var f in all)
        {
            if (used <= max) break;
            if (f.Id == _busy) continue;
            if (_files.TryRemove(f.Id, out _)) TryDelete(f.Path);
            used -= f.Size;
        }
    }

    // NTFS often doesn't update access times by itself, so "last played" is written explicitly.
    private static void Touch(string path)
    {
        try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static DateTime LastUsed(string path)
    {
        try { return File.GetLastAccessTimeUtc(path); } catch (IOException) { return DateTime.MinValue; }
    }

    private static long SizeOf(string path)
    {
        try { return new FileInfo(path).Length; } catch (IOException) { return 0; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose() => _stop.Cancel();
}
