using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using Tune.Core.Util;

namespace Tune.App.Platform;

/// <summary>
/// Album-art loader: disk cache (LRU-trimmed), decoded at display size and frozen, held only weakly in memory
/// so off-screen art can be collected — keeps RAM low.
/// </summary>
public sealed partial class ArtCache(string dir, HttpClient http, long maxBytes = 200L * 1024 * 1024)
{
    private readonly ConcurrentDictionary<string, WeakReference<BitmapSource>> _memory = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _downloads = new();

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex GoogleSize();

    [GeneratedRegex(@"/(default|mqdefault|hqdefault|sddefault)\.jpg")]
    private static partial Regex YtImgSize();

    /// <summary>Asks Google's image servers for roughly the size we draw (they resize server-side).</summary>
    public static string Sized(string url, int px)
    {
        if (GoogleSize().IsMatch(url)) return GoogleSize().Replace(url, $"=w{px}-h{px}");
        if (YtImgSize().IsMatch(url))
            return YtImgSize().Replace(url, px <= 120 ? "/mqdefault.jpg" : px <= 200 ? "/hqdefault.jpg" : "/maxresdefault.jpg");
        return url;
    }

    public async Task<BitmapSource?> GetAsync(string? url, int decodePx)
    {
        if (string.IsNullOrEmpty(url)) return null;
        var sized = Sized(url, decodePx);
        var key = sized + "|" + decodePx;
        if (_memory.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached)) return cached;

        var file = await _downloads.GetOrAdd(sized, u => DownloadAsync(u)).ConfigureAwait(false);
        _downloads.TryRemove(sized, out _);
        if (file is null) return null;

        try
        {
            var bmp = await Task.Run(() =>
            {
                var b = new BitmapImage();
                b.BeginInit();
                b.CacheOption = BitmapCacheOption.OnLoad;
                b.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
                b.DecodePixelWidth = decodePx;
                b.UriSource = new Uri(file);
                b.EndInit();
                b.Freeze();
                return (BitmapSource)b;
            }).ConfigureAwait(false);
            _memory[key] = new WeakReference<BitmapSource>(bmp);
            return bmp;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or InvalidOperationException or FileFormatException)
        {
            Log.Warn("Bad art file " + file, e);
            TryDelete(file);
            return null;
        }
    }

    /// <summary>Local path of a cached image (used for the Windows media flyout); downloads if needed.</summary>
    public Task<string?> GetFileAsync(string? url, int px) =>
        string.IsNullOrEmpty(url) ? Task.FromResult<string?>(null) : DownloadAsync(Sized(url, px));

    private async Task<string?> DownloadAsync(string url)
    {
        var file = Path.Combine(dir, Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(url))) + ".img");
        if (File.Exists(file))
        {
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            return file;
        }
        try
        {
            Directory.CreateDirectory(dir);
            var bytes = await GetWithFallbackAsync(url).ConfigureAwait(false);
            var tmp = file + ".part";
            await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
            File.Move(tmp, file, overwrite: true);
            NoteDownloaded(bytes.Length);
            return file;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
        {
            Log.Warn("Art download failed", e);
            return null;
        }
    }

    /// <summary>Not every video has an HD frame: maxresdefault → hq720 → hqdefault.</summary>
    private async Task<byte[]> GetWithFallbackAsync(string url)
    {
        string[] chain = url.Contains("/maxresdefault.jpg", StringComparison.Ordinal)
            ? [url, url.Replace("/maxresdefault.jpg", "/hq720.jpg"), url.Replace("/maxresdefault.jpg", "/hqdefault.jpg")]
            : [url];
        for (var i = 0; ; i++)
        {
            using var resp = await http.GetAsync(chain[i]).ConfigureAwait(false);
            if (resp.IsSuccessStatusCode || i == chain.Length - 1)
            {
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }
    }

    // Tune runs for days: besides the startup trim, trim again after every TrimEvery of new downloads
    private const long TrimEvery = 20L * 1024 * 1024;
    private long _sinceTrim;
    private int _trimming;

    private void NoteDownloaded(long bytes)
    {
        if (Interlocked.Add(ref _sinceTrim, bytes) < TrimEvery || Interlocked.Exchange(ref _trimming, 1) == 1) return;
        Interlocked.Exchange(ref _sinceTrim, 0);
        _ = Task.Run(() =>
        {
            try { Trim(); }
            finally { Interlocked.Exchange(ref _trimming, 0); }
        });
    }

    /// <summary>Deletes least-recently-used files until the cache is under its cap (at startup and as it grows).</summary>
    public void Trim()
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var files = new DirectoryInfo(dir).GetFiles().OrderBy(f => f.LastAccessTimeUtc).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var f in files)
            {
                if (total <= maxBytes) break;
                total -= f.Length;
                TryDelete(f.FullName);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Art cache trim failed", e);
        }
    }

    public long SizeBytes() =>
        Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles().Sum(f => f.Length) : 0;

    public void Clear()
    {
        _memory.Clear();
        if (!Directory.Exists(dir)) return;
        foreach (var f in Directory.GetFiles(dir)) TryDelete(f);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
