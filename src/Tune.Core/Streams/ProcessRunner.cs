using System.Diagnostics;
using System.Text;

namespace Tune.Core.Streams;

public interface IProcessRunner
{
    Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>Runs a console tool hidden, capturing UTF-8 output; kills the whole tree on timeout or cancel.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string exe, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Could not start {exe}");
        // Children (yt-dlp's own child, deno) inherit the job, so a Tune crash can't leave them running.
        KillOnExitJob.Add(p);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        var stdout = p.StandardOutput.ReadToEndAsync(cts.Token);
        var stderr = p.StandardError.ReadToEndAsync(cts.Token);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            return (p.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return (-1, "", $"ERROR: {Path.GetFileName(exe)} timed out after {timeout.TotalSeconds:0}s");
        }
    }
}
