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
/// <summary>Download progress for the first-run setup screen. Fraction is null when the size is unknown.</summary>
public sealed record ToolProgress(string Tool, double? Fraction);

/// <summary>Tune's own copy of python.exe plus the install it borrows its standard library from.</summary>
public sealed record PrivatePython(string Exe, string Home);

public sealed class ToolManager(string toolsDir, HttpClient http, IProcessRunner runner, LibraryStore settings, IClock clock, ToolManifest manifest)
{
    private const string LastUpdateKey = "ytdlp.lastUpdate";
    private static readonly TimeSpan UpdateInterval = TimeSpan.FromHours(24);

    public string YtDlpPath { get; } = Path.Combine(toolsDir, "yt-dlp.exe");
    public string DenoPath { get; } = Path.Combine(toolsDir, "deno.exe");

    public async Task EnsureInstalledAsync(IProgress<ToolProgress>? progress = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(toolsDir);
        if (!File.Exists(YtDlpPath))
        {
            Log.Info("Downloading yt-dlp");
            await DownloadAsync(new Uri(manifest.YtDlpUrl), YtDlpPath, "yt-dlp", progress, ct).ConfigureAwait(false);
        }

        var marker = DenoPath + ".version";
        if (!File.Exists(DenoPath) || !File.Exists(marker) || File.ReadAllText(marker) != manifest.DenoVersion)
        {
            Log.Info($"Downloading deno {manifest.DenoVersion}");
            var tgz = Path.Combine(toolsDir, "deno.download.tgz");
            try
            {
                // npm package @deno/win32-x64: gzip'd tar holding package/deno.exe, verified with npm's SRI hash
                await DownloadAsync(new Uri(manifest.DenoUrl), tgz, "deno", progress, ct).ConfigureAwait(false);
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
        Log.Info($"yt-dlp -U exit {exit}: {stdout.Trim()} {stderr.Trim()}");
        if (exit == 0) settings.SetSetting(LastUpdateKey, Unix(now)); // a failed (e.g. offline) update is retried next time
        return exit == 0;
    }

    // ---------- yt-dlp as a Python module (fast path: one long-lived worker, updates from PyPI) ----------

    private const string PyLibUpdateKey = "pylib.lastUpdate";

    /// <summary>Tune's private copy of the yt-dlp Python package (doesn't touch the user's own Python packages).</summary>
    public string PyLibDir { get; } = Path.Combine(toolsDir, "pylib");

    public bool PyLibInstalled => Directory.Exists(Path.Combine(PyLibDir, "yt_dlp"));

    /// <summary>First python.exe on PATH, skipping the Microsoft Store "install Python" stub.</summary>
    public static string? FindPython(string? pathEnv, Func<string, bool> fileExists)
    {
        foreach (var dir in (pathEnv ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (dir.Contains(@"\Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase)) continue;
            var candidate = Path.Combine(dir, "python.exe");
            if (fileExists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// A copy of the interpreter in Tune's folder, run with PYTHONHOME pointing at the real install.
    /// VPN split-tunnelling rules match programs by path: the user's python.exe goes through a VPN while
    /// mpv inside Tune.exe goes direct, and YouTube links only work from the IP that fetched them.
    /// </summary>
    public PrivatePython PreparePrivatePython(string python)
    {
        var home = Path.GetDirectoryName(python)!;
        var dir = Path.Combine(toolsDir, "py");
        Directory.CreateDirectory(dir);
        foreach (var src in Directory.GetFiles(home, "*.*")
                     .Where(f => Path.GetFileName(f) is var n && (n.Equals("python.exe", StringComparison.OrdinalIgnoreCase)
                                 || (n.StartsWith("python3", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                                 || (n.StartsWith("vcruntime", StringComparison.OrdinalIgnoreCase) && n.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)))))
        {
            // renamed: VPN split-tunnel rules may match "python.exe" by name, not only by path
            var name = Path.GetFileName(src).Equals("python.exe", StringComparison.OrdinalIgnoreCase) ? PrivatePythonName : Path.GetFileName(src);
            var dst = Path.Combine(dir, name);
            var s = new FileInfo(src);
            var d = new FileInfo(dst);
            if (!d.Exists || d.Length != s.Length || d.LastWriteTimeUtc < s.LastWriteTimeUtc)
                File.Copy(src, dst, overwrite: true);
        }
        var stale = Path.Combine(dir, "python.exe");
        if (File.Exists(stale)) File.Delete(stale);
        return new PrivatePython(Path.Combine(dir, PrivatePythonName), home);
    }

    private const string PrivatePythonName = "tune-resolver.exe";

    /// <summary>Installs (or with <paramref name="update"/> upgrades) yt-dlp into <see cref="PyLibDir"/> with pip.</summary>
    public async Task<bool> EnsurePyLibAsync(string python, bool update, CancellationToken ct = default)
    {
        if (!update && PyLibInstalled) return true;
        string[] args = ["-m", "pip", "install", "--quiet", "--disable-pip-version-check", "--upgrade", "--target", PyLibDir, "yt-dlp"];
        var (exit, _, stderr) = await runner.RunAsync(python, args, TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
        Log.Info($"pip yt-dlp ({(update ? "update" : "install")}) exit {exit} {stderr.Trim()}");
        if (exit == 0) settings.SetSetting(PyLibUpdateKey, Unix(clock.Now));
        return exit == 0;
    }

    /// <summary>Upgrades the Python yt-dlp at most once a day. True when an upgrade ran successfully.</summary>
    public async Task<bool> UpdatePyLibIfDueAsync(string python, CancellationToken ct = default)
    {
        if (long.TryParse(settings.GetSetting(PyLibUpdateKey), out var last)
            && clock.Now - DateTimeOffset.FromUnixTimeSeconds(last) < UpdateInterval)
            return false;
        return await EnsurePyLibAsync(python, update: true, ct).ConfigureAwait(false);
    }

    private static string Unix(DateTimeOffset t) => t.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task DownloadAsync(Uri url, string target, string tool, IProgress<ToolProgress>? progress, CancellationToken ct)
    {
        var tmp = target + ".part";
        try
        {
            using (var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                var total = resp.Content.Headers.ContentLength;
                await using var fs = File.Create(tmp);
                await using var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                var buffer = new byte[81920];
                long done = 0;
                double? lastReported = -1;
                int read;
                progress?.Report(new ToolProgress(tool, total is > 0 ? 0 : null));
                while ((read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    done += read;
                    double? fraction = total is > 0 ? Math.Min(1.0, (double)done / total.Value) : null;
                    if (fraction is null || fraction - lastReported >= 0.01)
                    {
                        lastReported = fraction;
                        progress?.Report(new ToolProgress(tool, fraction));
                    }
                }
                progress?.Report(new ToolProgress(tool, 1.0));
            }
            File.Move(tmp, target, overwrite: true);
        }
        finally
        {
            File.Delete(tmp);
        }
    }
}
