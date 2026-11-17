using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Tune.Core.Util;

namespace Tune.App.Platform;

/// <summary>
/// Developer check (env TUNE_SCROLLBENCH=1): scrolls the biggest page down and back up with the normal
/// smooth scroll and logs how many frames were drawn and how long the slowest ones took.
/// </summary>
public static class ScrollBench
{
    public static void StartIfAsked(Window window)
    {
        if (Environment.GetEnvironmentVariable("TUNE_SCROLLBENCH") != "1") return;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        t.Tick += (_, _) => { t.Stop(); Run(window); };
        t.Start();
    }

    private static void Run(Window window)
    {
        var sv = Biggest(window);
        if (sv is null) { Log.Info("scrollbench: no scroll viewer"); return; }
        var mode = Environment.GetEnvironmentVariable("TUNE_SCROLLBENCH_MODE") ?? "";
        var touched = 0;
        if (mode != "") foreach (var el in All(window))
            if (el is UIElement { Effect: not null } u)
            {
                touched++;
                if (mode == "count") Log.Info($"scrollbench effect: {u.GetType().Name} {(u as FrameworkElement)?.Name} {u.Effect.GetType().Name} visible={u.IsVisible} opacity={u.Opacity} in {(VisualTreeHelper.GetParent(u) as FrameworkElement)?.TemplatedParent?.GetType().Name}/{((u as FrameworkElement)?.DataContext)?.GetType().Name}");
                if (mode == "noeffects") u.Effect = null;
                else if (mode == "cache") u.CacheMode = new BitmapCache();
            }
        Log.Info($"scrollbench mode '{mode}', {touched} effects");
        var frames = new List<double>();
        var last = TimeSpan.Zero;
        var sw = Stopwatch.StartNew();
        double prev = 0;
        var step = 0;
        void OnFrame(object? s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last || step > 60) return;
            last = now;
            var ms = sw.Elapsed.TotalMilliseconds;
            if (prev > 0) frames.Add(ms - prev);
            prev = ms;
        }
        CompositionTarget.Rendering += OnFrame;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            step++;
            if (step <= 30) SmoothScroll.ScrollBy(sv, 120);
            else if (step <= 60) SmoothScroll.ScrollBy(sv, -120);
            else
            {
                timer.Stop();
                CompositionTarget.Rendering -= OnFrame;
                var slow = frames.Count(f => f > 20);
                var verySlow = frames.Count(f => f > 34);
                frames.Sort();
                var secs = sw.Elapsed.TotalSeconds;
                double P(double q) => frames.Count == 0 ? 0 : frames[(int)Math.Min(frames.Count - 1, q * frames.Count)];
                Log.Info($"scrollbench: {frames.Count} frames in {secs:0.0}s = {frames.Count / secs:0} fps; " +
                         $"median {P(0.5):0.0} ms, p90 {P(0.9):0.0} ms, p99 {P(0.99):0.0} ms, max {(frames.Count > 0 ? frames[^1] : 0):0.0} ms; " +
                         $"slow(>20ms) {slow}, very slow(>34ms) {verySlow}; tier {RenderCapability.Tier >> 16}, extent {sv.ExtentHeight:0}");
            }
        };
        timer.Start();
    }

    private static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            yield return d;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
    }

    private static ScrollViewer? Biggest(DependencyObject root)
    {
        ScrollViewer? best = null;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var d = stack.Pop();
            if (d is ScrollViewer { IsVisible: true } sv && sv.ScrollableHeight > 0
                && (best is null || sv.ActualWidth * sv.ActualHeight > best.ActualWidth * best.ActualHeight)) best = sv;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) stack.Push(VisualTreeHelper.GetChild(d, i));
        }
        return best;
    }
}
