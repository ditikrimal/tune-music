using System.Collections.Concurrent;
using System.Net;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Streams;
using Tune.Core.Util;

namespace Tune.Core.Offline;

public enum DownloadState { None, Queued, Downloading, Done }

/// <summary>What playback needs to know about offline songs.</summary>
public interface IOfflineSource
{
    /// <summary>The downloaded audio file, or null.</summary>
    string? LocalPath(string videoId);

    IConnectivity Network { get; }

    /// <summary>Downloaded songs, newest first.</summary>
    IReadOnlyList<Track> DownloadedTracks();
}

/// <summary>
/// Saves songs for offline play: one at a time, in the order asked, surviving restarts (the list lives in
/// the library database). Waits while offline and carries on when the connection returns.
/// </summary>
public sealed class DownloadManager : IOfflineSource, IDisposable
{
    private const long ChunkBytes = 4L * 1024 * 1024; // googlevideo throttles long single requests; ranged chunks stay fast
    private const int MaxAttempts = 3;

    private readonly LibraryStore _lib;
    private readonly IStreamResolver _resolver;
    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly IClock _clock;
    private readonly Func<Track, string, CancellationToken, Task<string?>>? _saveArt;

    private readonly ConcurrentDictionary<string, string> _files = new();      // videoId → audio file
    private readonly ConcurrentDictionary<string, string> _artByThumb = new(); // thumbnail URL → saved art
    private readonly Lock _gate = new();
    private readonly List<Track> _queue = [];
    private readonly Dictionary<string, int> _attempts = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stop = new();
    private (string Id, CancellationTokenSource Cts)? _active;
    private double _activeProgress;

    public DownloadManager(LibraryStore lib, IStreamResolver resolver, HttpClient http, string dir, IConnectivity network,
        IClock clock, Func<Track, string, CancellationToken, Task<string?>>? saveArt = null)
    {
        _lib = lib;
        _resolver = resolver;
        _http = http;
        _dir = dir;
        Network = network;
        _clock = clock;
        _saveArt = saveArt;
        Network.Changed += online =>
        {
            if (online) _wake.Release();
        };
    }

    public IConnectivity Network { get; }

    /// <summary>A song's download state or progress changed (any thread). Argument: video id.</summary>
    public event Action<string>? Changed;

    /// <summary>A download gave up (any thread). Argument: a message for a toast.</summary>
    public event Action<string>? Failed;

    /// <summary>Loads the saved list and starts the background downloader.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_dir);
        foreach (var d in _lib.GetDownloads().Reverse()) // oldest first, so the queue keeps the asked order
        {
            if (d.Path is not null && File.Exists(d.Path))
            {
                _files[d.Track.VideoId] = d.Path;
                if (d.ArtPath is not null && d.Track.ThumbnailUrl is { } thumb && File.Exists(d.ArtPath)) _artByThumb[thumb] = d.ArtPath;
            }
            else
            {
                lock (_gate) _queue.Add(d.Track); // never finished, or the file was deleted: fetch it again
            }
        }
        foreach (var part in Directory.EnumerateFiles(_dir, "*.part")) TryDelete(part);
        _ = Task.Run(WorkAsync);
    }

    public string? LocalPath(string videoId) => _files.TryGetValue(videoId, out var p) ? p : null;

    public bool IsDownloaded(string videoId) => _files.ContainsKey(videoId);

    /// <summary>Saved cover art for a thumbnail URL (so pictures show while offline).</summary>
    public string? ArtFor(string thumbnailUrl) => _artByThumb.TryGetValue(thumbnailUrl, out var p) ? p : null;

    public DownloadState StateOf(string videoId)
    {
        if (_files.ContainsKey(videoId)) return DownloadState.Done;
        lock (_gate)
        {
            if (_active?.Id == videoId) return DownloadState.Downloading;
            return _queue.Any(t => t.VideoId == videoId) ? DownloadState.Queued : DownloadState.None;
        }
    }

    /// <summary>0..1 for the song being downloaded right now.</summary>
    public double ProgressOf(string videoId)
    {
        lock (_gate) return _active?.Id == videoId ? _activeProgress : 0;
    }

    /// <summary>Songs waiting or downloading.</summary>
    public int PendingCount
    {
        get { lock (_gate) return _queue.Count; }
    }

    public IReadOnlyList<Track> DownloadedTracks() =>
        _lib.GetDownloads().Where(d => _files.ContainsKey(d.Track.VideoId)).Select(d => d.Track).ToList();

    /// <summary>Everything on the download list (done or not), newest first.</summary>
    public IReadOnlyList<DownloadInfo> All() => _lib.GetDownloads();

    public long DownloadedBytes() => _lib.GetDownloads().Where(d => _files.ContainsKey(d.Track.VideoId)).Sum(d => d.Bytes);

    public void Enqueue(IEnumerable<Track> tracks)
    {
        var fresh = new List<Track>();
        lock (_gate)
        {
            foreach (var t in tracks.DistinctBy(t => t.VideoId))
            {
                if (_files.ContainsKey(t.VideoId) || _queue.Any(q => q.VideoId == t.VideoId)) continue;
                _queue.Add(t);
                _attempts.Remove(t.VideoId);
                fresh.Add(t);
            }
        }
        if (fresh.Count == 0) return;
        _lib.AddDownloads(fresh, _clock.Now);
        foreach (var t in fresh) Changed?.Invoke(t.VideoId);
        _wake.Release();
    }

    /// <summary>Deletes the song's file (or stops its download) and takes it off the list.</summary>
    public void Remove(IEnumerable<string> videoIds)
    {
        foreach (var id in videoIds.ToList())
        {
            lock (_gate)
            {
                _queue.RemoveAll(t => t.VideoId == id);
                if (_active?.Id == id) _active.Value.Cts.Cancel();
            }
            if (_files.TryRemove(id, out var path)) TryDelete(path);
            foreach (var kv in _artByThumb.Where(kv => Path.GetFileNameWithoutExtension(kv.Value) == id).ToList())
            {
                _artByThumb.TryRemove(kv.Key, out _);
                TryDelete(kv.Value);
            }
            _lib.RemoveDownload(id);
            Changed?.Invoke(id);
        }
    }

    // ---------- worker ----------

    private async Task WorkAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            Track? next;
            lock (_gate) next = _queue.FirstOrDefault();
            if (next is null || !Network.IsOnline)
            {
                try { await _wake.WaitAsync(next is null ? Timeout.InfiniteTimeSpan : TimeSpan.FromMinutes(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate)
            {
                _active = (next.VideoId, cts);
                _activeProgress = 0;
            }
            Changed?.Invoke(next.VideoId);
            try
            {
                await DownloadAsync(next, cts.Token).ConfigureAwait(false);
                lock (_gate) _queue.RemoveAll(t => t.VideoId == next.VideoId);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // removed by the user while downloading
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                Log.Warn($"Download failed for {next.VideoId}", e);
                if (!await Network.CheckNowAsync().ConfigureAwait(false))
                {
                    // the connection went: keep it queued, the loop waits until we're back
                }
                else
                {
                    int attempts;
                    lock (_gate)
                    {
                        attempts = _attempts[next.VideoId] = _attempts.GetValueOrDefault(next.VideoId) + 1;
                        _queue.RemoveAll(t => t.VideoId == next.VideoId);
                        if (attempts < MaxAttempts) _queue.Add(next); // try the others first
                    }
                    if (attempts >= MaxAttempts)
                    {
                        _lib.RemoveDownload(next.VideoId);
                        Failed?.Invoke($"Couldn't download {next.Title}");
                    }
                }
            }
            finally
            {
                lock (_gate) _active = null;
                Changed?.Invoke(next.VideoId);
            }
        }
    }

    private async Task DownloadAsync(Track track, CancellationToken ct)
    {
        var id = track.VideoId;
        var url = await _resolver.ResolveAsync(id, ct).ConfigureAwait(false);
        var part = Path.Combine(_dir, id + ".part");
        long bytes;
        try
        {
            bytes = await FetchAsync(url, part, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException e) when (e.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Gone or HttpStatusCode.NotFound)
        {
            // a stale or wrongly-routed link: get a fresh one once
            _resolver.ReportBroken(id);
            _resolver.Invalidate(id);
            try
            {
                url = await _resolver.ResolveAsync(id, ct).ConfigureAwait(false);
                bytes = await FetchAsync(url, part, ct).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(part);
                throw;
            }
        }
        catch
        {
            TryDelete(part);
            throw;
        }

        var file = Path.Combine(_dir, id + ExtensionFor(url));
        File.Move(part, file, overwrite: true);

        string? art = null;
        if (_saveArt is not null && track.ThumbnailUrl is { } thumb)
        {
            try { art = await _saveArt(track, Path.Combine(_dir, id + ".jpg"), ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Art for download {id} failed", e); }
            if (art is not null) _artByThumb[thumb] = art;
        }

        _lib.SetDownloaded(id, file, art, bytes);
        _files[id] = file;
        Log.Info($"Downloaded {id} ({bytes / 1024} KB)");
    }

    /// <summary>Downloads in ranged chunks, reporting progress. Returns the size.</summary>
    private async Task<long> FetchAsync(Uri url, string target, CancellationToken ct)
    {
        long? total = ContentLength(url);
        long done = 0;
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        while (total is null || done < total)
        {
            var end = done + ChunkBytes - 1;
            if (total is { } t) end = Math.Min(end, t - 1);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, end);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && total is null) break; // we read it all
            resp.EnsureSuccessStatusCode();
            total ??= resp.Content.Headers.ContentRange?.Length ?? (resp.StatusCode == HttpStatusCode.OK ? resp.Content.Headers.ContentLength : null);
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var before = done;
            var buffer = new byte[1 << 16];
            int n;
            while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
            }
            if (done == before) throw new IOException("The server sent no data");
            if (total is { } tt)
            {
                lock (_gate) _activeProgress = Math.Clamp((double)done / tt, 0, 1);
                if (_active is { } a) Changed?.Invoke(a.Id);
            }
            if (resp.StatusCode == HttpStatusCode.OK) break; // the server ignored the range and sent everything
        }
        if (total is { } size && done < size) throw new IOException($"Download stopped at {done} of {size} bytes");
        return done;
    }

    /// <summary>googlevideo links carry the size ("clen") and type ("mime") in the query string.</summary>
    public static long? ContentLength(Uri url) =>
        long.TryParse(Query(url, "clen"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : null;

    public static string ExtensionFor(Uri url) => Query(url, "mime") switch
    {
        "audio/webm" => ".webm",
        "audio/mp4" => ".m4a",
        _ => ".audio",
    };

    private static string? Query(Uri url, string key)
    {
        foreach (var pair in url.Query.TrimStart('?').Split('&'))
        {
            var i = pair.IndexOf('=');
            if (i > 0 && pair[..i] == key) return Uri.UnescapeDataString(pair[(i + 1)..]);
        }
        return null;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        lock (_gate) _active?.Cts.Cancel();
    }
}
