using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Tune.Core.Library;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.Streams;

public sealed class ToolInstallException(string message) : Exception(message);

public sealed record ToolManifest(string DenoVersion, string DenoUrl, string DenoIntegrity, string YtDlpUrl)
{
    /// <summary>Reads tools/manifest.json, embedded into this assembly at build time.</summary>
    public static ToolManifest Load()
    {
        using var s = typeof(ToolManifest).Assembly.GetManifestResourceStream("manifest.json")
                      ?? throw new InvalidOperationException("manifest.json not embedded");
        var j = JsonNode.Parse(s)!;
        return new ToolManifest(
            (string)j["deno"]!["version"]!, (string)j["deno"]!["url"]!, (string)j["deno"]!["integrity"]!,
            (string)j["ytdlp"]!["url"]!);
    }
}

/// <summary>Keeps the bundled yt-dlp and deno in the tools folder: installs missing ones and updates yt-dlp.</summary>
public sealed class ToolManager(string toolsDir, HttpClient http, IProcessRunner runner, LibraryStore settings, IClock clock, ToolManifest manifest)
{
    private const string LastUpdateKey = "ytdlp.lastUpdate";
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    public string YtDlpPath { get; } = Path.Combine(toolsDir, "yt-dlp.exe");
    public string DenoPath { get; } = Path.Combine(toolsDir, "deno.exe");

    public async Task EnsureInstalledAsync(CancellationToken ct = default)
    {
        Directory.CreateDirectory(toolsDir);
        if (!File.Exists(YtDlpPath))
        {
            Log.Info("Downloading yt-dlp");
            await DownloadAsync(new Uri(manifest.YtDlpUrl), YtDlpPath, ct).ConfigureAwait(false);
        }

        var marker = DenoPath + ".version";
        if (!File.Exists(DenoPath) || !File.Exists(marker) || File.ReadAllText(marker) != manifest.DenoVersion)
        {
            Log.Info($"Downloading deno {manifest.DenoVersion}");
            var tgz = Path.Combine(toolsDir, "deno.download.tgz");
            try
            {
                // npm package @deno/win32-x64: gzip'd tar holding package/deno.exe, verified with npm's SRI hash
                await DownloadAsync(new Uri(manifest.DenoUrl), tgz, ct).ConfigureAwait(false);
                string actual;
                await using (var fs = File.OpenRead(tgz))
                    actual = "sha512-" + Convert.ToBase64String(await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false));
                if (actual != manifest.DenoIntegrity)
                    throw new ToolInstallException($"deno integrity mismatch (expected {manifest.DenoIntegrity}, got {actual})");

                await using (var fs = File.OpenRead(tgz))
                await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                await using (var tar = new TarReader(gz))
                {
                    var found = false;
                    while (await tar.GetNextEntryAsync(cancellationToken: ct).ConfigureAwait(false) is { } entry)
                    {
                        if (entry.Name != "package/deno.exe" || entry.DataStream is null) continue;
                        await using (var outFile = File.Create(DenoPath))
                            await entry.DataStream.CopyToAsync(outFile, ct).ConfigureAwait(false);
                        found = true;
                        break;
                    }
                    if (!found) throw new ToolInstallException("deno.exe missing from package");
                }
                await File.WriteAllTextAsync(marker, manifest.DenoVersion, ct).ConfigureAwait(false);
            }
            finally
            {
                File.Delete(tgz);
            }
        }
    }

    /// <summary>Runs <c>yt-dlp -U</c> at most once a day (or whenever <paramref name="force"/> is set).</summary>
    public async Task<bool> UpdateYtDlpIfDueAsync(bool force, CancellationToken ct = default)
    {
        var now = clock.Now;
        if (!force && long.TryParse(settings.GetSetting(LastUpdateKey), out var last)
                   && now - DateTimeOffset.FromUnixTimeSeconds(last) < UpdateInterval)
            return false;

        var (exit, stdout, stderr) = await runner.RunAsync(YtDlpPath, ["-U"], TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
        settings.SetSetting(LastUpdateKey, now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
        Log.Info($"yt-dlp -U exit {exit}: {stdout.Trim()} {stderr.Trim()}");
        return exit == 0;
    }

    private async Task DownloadAsync(Uri url, string target, CancellationToken ct)
    {
        var tmp = target + ".part";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                await using var fs = File.Create(tmp);
                await resp.Content.CopyToAsync(fs, ct).ConfigureAwait(false);
            }
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
