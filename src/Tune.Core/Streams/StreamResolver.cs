using System.Collections.Concurrent;
using System.Web;
using Tune.Core.Playback;

namespace Tune.Core.Streams;

public interface IStreamResolver
{
    Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default);
    void Invalidate(string videoId);
}

public sealed class StreamResolveException(string videoId, string reason) : Exception(reason)
{
    public string VideoId { get; } = videoId;
}

/// <summary>Turns a video id into a direct audio URL with yt-dlp, caching it until shortly before it expires.</summary>
public sealed class StreamResolver(ToolManager tools, IProcessRunner runner, IClock clock) : IStreamResolver
{
    private static readonly TimeSpan Safety = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (Uri Url, DateTimeOffset ValidUntil)> _cache = new();

    public async Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(videoId, out var hit) && clock.Now < hit.ValidUntil) return hit.Url;

        string[] args =
        [
            "-f", "bestaudio", "-g", "--no-playlist", "--no-warnings", "--no-progress",
            "--js-runtimes", "deno:" + tools.DenoPath,
            "https://music.youtube.com/watch?v=" + videoId,
        ];
        var (exit, stdout, stderr) = await runner.RunAsync(tools.YtDlpPath, args, Timeout, ct).ConfigureAwait(false);
        if (exit != 0)
            throw new StreamResolveException(videoId, LastError(stderr) ?? $"yt-dlp exited with {exit}");

        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null || !Uri.TryCreate(line, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
            throw new StreamResolveException(videoId, "yt-dlp returned no stream URL");

        var validUntil = long.TryParse(HttpUtility.ParseQueryString(url.Query)["expire"], out var expire)
            ? DateTimeOffset.FromUnixTimeSeconds(expire) - Safety
            : clock.Now + DefaultLifetime;
        _cache[videoId] = (url, validUntil);
        return url;
    }

    public void Invalidate(string videoId) => _cache.TryRemove(videoId, out _);

    private static string? LastError(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal))
        ?? stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
