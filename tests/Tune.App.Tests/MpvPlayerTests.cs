using System.IO;
using System.Diagnostics;
using Tune.App.Audio;

namespace Tune.App.Tests;

public sealed class ToneFixture : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tune-tone-5s.webm");

    public ToneFixture()
    {
        if (File.Exists(Path)) return;
        using var p = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-y -loglevel error -f lavfi -i sine=f=440:d=5 -c:a libopus \"{Path}\"") { CreateNoWindow = true })!;
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed to generate the test tone");
    }

    public void Dispose() { }
}

[Trait("Category", "Integration")]
public class MpvPlayerTests(ToneFixture tone) : IClassFixture<ToneFixture>
{
    private static async Task WaitFor(Func<bool> condition, int timeoutMs = 8000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("condition not met");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Loads_and_reports_duration_5s()
    {
        using var p = new MpvPlayer();
        p.Volume = 0;
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.Zero);
        p.Play();
        await WaitFor(() => p.Duration is not null);
        Assert.InRange(p.Duration!.Value.TotalSeconds, 4.7, 5.3);
        Assert.False(p.IsPaused);
    }

    [Fact]
    public async Task Start_at_and_seek_then_position()
    {
        using var p = new MpvPlayer();
        p.Volume = 0;
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.FromSeconds(1));
        p.Pause();
        await WaitFor(() => p.Position.TotalSeconds >= 0.9);
        p.Seek(TimeSpan.FromSeconds(3));
        await WaitFor(() => Math.Abs(p.Position.TotalSeconds - 3) < 0.2);
    }

    [Fact]
    public async Task Ended_fires_at_end_and_position_events_flow()
    {
        using var p = new MpvPlayer();
        p.Volume = 0;
        var ended = 0;
        var positions = 0;
        p.Ended += () => ended++;
        p.PositionChanged += () => positions++;
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.FromSeconds(4));
        p.Play();
        await WaitFor(() => ended == 1, 6000);
        Assert.True(positions > 0);
    }

    [Fact]
    public async Task Bad_url_fires_Failed_not_Ended()
    {
        using var p = new MpvPlayer();
        string? failure = null;
        var ended = false;
        p.Failed += why => failure = why;
        p.Ended += () => ended = true;
        await p.LoadAsync(new Uri(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "does-not-exist.webm")), TimeSpan.Zero);
        p.Play();
        await WaitFor(() => failure is not null);
        Assert.False(ended);
    }

    [Fact]
    public async Task Stop_does_not_fire_Ended()
    {
        using var p = new MpvPlayer();
        p.Volume = 0;
        var ended = false;
        p.Ended += () => ended = true;
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.Zero);
        p.Play();
        await WaitFor(() => p.Duration is not null);
        p.Stop();
        await Task.Delay(300);
        Assert.False(ended);
    }

    [Fact]
    public async Task Stop_clears_duration_and_position()
    {
        using var p = new MpvPlayer();
        p.Volume = 0;
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.FromSeconds(2));
        p.Play();
        await WaitFor(() => p.Duration is not null);
        p.Stop();
        Assert.Null(p.Duration);
        Assert.Equal(TimeSpan.Zero, p.Position);
    }

    [Fact]
    public async Task Loading_next_song_does_not_fire_Ended_for_previous()
    {
        using var p = new MpvPlayer();
        p.Volume = 0;
        var ended = 0;
        p.Ended += () => ended++;
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.Zero);
        p.Play();
        await WaitFor(() => p.Duration is not null);
        await p.LoadAsync(new Uri(tone.Path), TimeSpan.FromSeconds(4.5));
        await WaitFor(() => ended == 1, 4000);
        await Task.Delay(300);
        Assert.Equal(1, ended);
    }
}
