using System.Net;

namespace Tune.Core.Offline;

/// <summary>Fetches a googlevideo audio link into a file in 4 MB ranged requests (long single requests get throttled).</summary>
public static class ChunkedDownload
{
    private const long ChunkBytes = 4L * 1024 * 1024;

    /// <summary>Downloads <paramref name="url"/> to <paramref name="target"/>; reports 0..1 a few times a second. Returns the size.</summary>
    public static async Task<long> FetchAsync(HttpClient http, Uri url, string target, Action<double>? progress, CancellationToken ct)
    {
        long? total = DownloadManager.ContentLength(url);
        long done = 0;
        await using var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        while (total is null || done < total)
        {
            var end = done + ChunkBytes - 1;
            if (total is { } t) end = Math.Min(end, t - 1);
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(done, end);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && total is null) break; // we read it all
            resp.EnsureSuccessStatusCode();
            total ??= resp.Content.Headers.ContentRange?.Length ?? (resp.StatusCode == HttpStatusCode.OK ? resp.Content.Headers.ContentLength : null);
            await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var before = done;
            var buffer = new byte[1 << 16];
            int n;
            var lastTick = Environment.TickCount64;
            while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                // report a few times a second: a whole song is often a single chunk
                if (progress is not null && total is { } totalBytes && Environment.TickCount64 - lastTick > 150)
                {
                    lastTick = Environment.TickCount64;
                    progress(Math.Clamp((double)done / totalBytes, 0, 1));
                }
            }
            if (done == before) throw new IOException("The server sent no data");
            if (progress is not null && total is { } tt) progress(Math.Clamp((double)done / tt, 0, 1));
            if (resp.StatusCode == HttpStatusCode.OK) break; // the server ignored the range and sent everything
        }
        if (total is { } size && done < size) throw new IOException($"Download stopped at {done} of {size} bytes");
        return done;
    }
}
