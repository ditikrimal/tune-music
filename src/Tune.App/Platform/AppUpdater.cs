using System.Diagnostics;
using System.IO;
using System.Net.Http;
using Tune.Core.Update;
using Tune.Core.Util;

namespace Tune.App.Platform;

/// <summary>
/// Keeps Tune.exe current: asks GitHub for the newest release, downloads the new Tune.exe next to the data folder,
/// swaps it in for the running one (Windows lets a running exe be renamed, not deleted) and restarts.
/// </summary>
public sealed class AppUpdater(HttpClient http, string dataDir)
{
    public const string AssetName = "Tune.exe";

    public static Version Current => typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>
    /// The shared single-file Tune.exe can replace itself. A build with its files spread out (Tune.dll next to the
    /// exe: developer builds, tools\publish.ps1) can't be swapped by one file, so it just opens the release page.
    /// </summary>
    public static bool CanSelfUpdate => Environment.ProcessPath is { } exe
        && !File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "Tune.dll"));

    // testing: point at a local copy of the release JSON
    private static string? FeedOverride => Environment.GetEnvironmentVariable("TUNE_UPDATE_FEED");

    public Task<ReleaseInfo?> CheckAsync(CancellationToken ct = default) =>
        ReleaseFeed.CheckAsync(http, Current, AssetName, FeedOverride, ct);

    /// <summary>Downloads the release's Tune.exe; returns the file. <paramref name="progress"/> gets 0..1.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, Action<double> progress, CancellationToken ct = default)
    {
        if (release.AssetUrl is null) throw new InvalidOperationException("This release has no Tune.exe");
        var dir = Path.Combine(dataDir, "update");
        Directory.CreateDirectory(dir);
        foreach (var old in Directory.EnumerateFiles(dir)) TryDelete(old);
        var target = Path.Combine(dir, $"Tune-{release.Version}.exe");
        var part = target + ".part";

        using var resp = await http.GetAsync(release.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength ?? release.AssetBytes;
        await using (var body = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            var buffer = new byte[1 << 16];
            long done = 0;
            int n;
            var tick = Environment.TickCount64;
            while ((n = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                if (total > 0 && Environment.TickCount64 - tick > 200)
                {
                    tick = Environment.TickCount64;
                    progress(Math.Clamp((double)done / total, 0, 1));
                }
            }
            if (total > 0 && done != total) throw new IOException($"Update download stopped at {done} of {total} bytes");
        }
        // a Windows program starts with "MZ": don't swap in an error page
        await using (var check = File.OpenRead(part))
            if (check.ReadByte() != 'M' || check.ReadByte() != 'Z') throw new IOException("The downloaded update isn't a program");
        File.Move(part, target, overwrite: true);
        progress(1);
        return target;
    }

    /// <summary>
    /// Puts the new exe where the running one is and starts it; the caller then quits. The new copy waits for this
    /// process to exit before taking over (see <see cref="WaitForPrevious"/>).
    /// </summary>
    public static void ApplyAndRestart(string newExe)
    {
        var exe = Environment.ProcessPath!;
        var parked = exe + ".old";
        var launch = exe;
        try
        {
            TryDelete(parked);
            File.Move(exe, parked); // allowed while running
            try { File.Copy(newExe, exe); }
            catch
            {
                File.Move(parked, exe); // put the old one back
                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Tune's folder isn't writable (e.g. Program Files): run the new version from the update folder instead
            Log.Warn("Couldn't replace Tune.exe in place; starting the downloaded copy", e);
            launch = newExe;
        }
        Process.Start(new ProcessStartInfo(launch, $"--after-update {Environment.ProcessId}") { UseShellExecute = false });
    }

    /// <summary>Started by <see cref="ApplyAndRestart"/>: wait until the old Tune has quit, then tidy up.</summary>
    public static void WaitForPrevious(string[] args)
    {
        var i = Array.IndexOf(args, "--after-update");
        if (i < 0) return;
        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try
            {
                using var old = Process.GetProcessById(pid);
                old.WaitForExit(30_000);
            }
            catch (ArgumentException) { } // already gone
        }
        if (Environment.ProcessPath is { } exe)
            for (var k = 0; k < 10 && File.Exists(exe + ".old"); k++)
            {
                TryDelete(exe + ".old");
                if (File.Exists(exe + ".old")) Thread.Sleep(300);
            }
    }

    /// <summary>Leftover from an update that finished while Tune was restarting.</summary>
    public static void CleanUp()
    {
        if (Environment.ProcessPath is { } exe) TryDelete(exe + ".old");
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { Log.Warn("Couldn't open " + url, e); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
