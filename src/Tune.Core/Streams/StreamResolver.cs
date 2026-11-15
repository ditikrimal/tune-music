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

/// <summary>
/// Turns a video id into a direct audio URL with yt-dlp, caching it until shortly before it expires.
/// YouTube keeps a fresh URL locked (HTTP 403) for the length of the pre-roll ad the client skipped;
/// yt-dlp reports that moment as <c>available_at</c> and we wait for it before handing the URL out.
/// </summary>
public sealed class StreamResolver(ToolManager tools, IProcessRunner runner, IClock clock,
    Func<TimeSpan, CancellationToken, Task>? delay = null) : IStreamResolver
{
    private static readonly TimeSpan AvailabilityMargin = TimeSpan.FromSeconds(0.5);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private static readonly TimeSpan Safety = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, (Uri Url, DateTimeOffset ValidUntil, DateTimeOffset AvailableAt)> _cache = new();

    public async Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(videoId, out var hit) && clock.Now < hit.ValidUntil)
        {
            await WaitUntilAvailableAsync(hit.AvailableAt, ct).ConfigureAwait(false);
            return hit.Url;
        }

        string[] args =
        [
            "-f", "bestaudio", "--no-playlist", "--no-warnings", "--no-progress",
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
        var availableAtUnix = lines.Select(l => long.TryParse(l, out var t) ? t : 0).FirstOrDefault(t => t > 0);
        var availableAt = availableAtUnix > 0 ? DateTimeOffset.FromUnixTimeSeconds(availableAtUnix) : DateTimeOffset.MinValue;

        var validUntil = long.TryParse(HttpUtility.ParseQueryString(url.Query)["expire"], out var expire)
            ? DateTimeOffset.FromUnixTimeSeconds(expire) - Safety
            : clock.Now + DefaultLifetime;
        _cache[videoId] = (url, validUntil, availableAt);
        await WaitUntilAvailableAsync(availableAt, ct).ConfigureAwait(false);
        return url;
    }

    private Task WaitUntilAvailableAsync(DateTimeOffset availableAt, CancellationToken ct)
    {
        var wait = availableAt - clock.Now;
        return wait > TimeSpan.Zero ? _delay(wait + AvailabilityMargin, ct) : Task.CompletedTask;
    }

    public void Invalidate(string videoId) => _cache.TryRemove(videoId, out _);

    private static string? LastError(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR", StringComparison.Ordinal))
        ?? stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
