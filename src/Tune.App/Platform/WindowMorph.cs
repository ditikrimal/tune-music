using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Tune.Core.Util;

namespace Tune.App.Platform;

/// <summary>
/// Animates one window turning into another (Tune's window into the mini-player, into full screen, and back):
/// a borderless see-through overlay shows a picture of the first window exactly where it is, the real windows
/// switch underneath while it covers them (cloaked, so nothing flashes), then the picture slides/resizes onto
/// the new spot while crossfading into a picture of the result. Heavy work (pictures, showing windows) all
/// happens before the clock starts, so every animated frame only moves two images.
/// </summary>
public static class WindowMorph
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(320);
    private static Window? _overlay;
    private static Border _box = null!;
    private static Image _a = null!, _b = null!;
    private static bool _busy;

    public static bool Busy => _busy;

    /// <summary>Creates the overlay ahead of time (first use would otherwise stall ~100 ms).</summary>
    public static void Warm()
    {
        if (_overlay is not null) return;
        _a = new Image { Stretch = Stretch.Fill };
        _b = new Image { Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(_a, BitmapScalingMode.Linear);
        RenderOptions.SetBitmapScalingMode(_b, BitmapScalingMode.Linear);
        var grid = new Grid();
        grid.Children.Add(_a);
        grid.Children.Add(_b);
        _box = new Border { ClipToBounds = true, Child = grid };
        var canvas = new Canvas();
        canvas.Children.Add(_box);
        // Not a layered (AllowsTransparency) window: those are copied through the CPU every frame, which at monitor
        // size costs ~20 ms a frame. Instead the DWM composites a normal GPU-rendered window with per-pixel alpha
        // (frame extended over the whole client area, transparent Direct3D clear colour).
        _overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = false, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Left = -20000, Top = -20000, Width = 10, Height = 10, Content = canvas, IsHitTestVisible = false,
        };
        var hwnd = new WindowInteropHelper(_overlay).EnsureHandle();
        var source = HwndSource.FromHwnd(hwnd);
        source.CompositionTarget.BackgroundColor = Colors.Transparent;
        source.AddHook((IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled) =>
        {
            if (msg == 0x0084) { handled = true; return new IntPtr(-1); } // WM_NCHITTEST: HTTRANSPARENT, clicks fall through
            return IntPtr.Zero;
        });
        var margins = new Margins { L = -1, R = -1, T = -1, B = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
        int v;
        v = 1; DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE: do not round */, ref v, sizeof(int));
        v = unchecked((int)0xFFFFFFFE); DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR: none */, ref v, sizeof(int));
        v = 1; DwmSetWindowAttribute(hwnd, 38 /* DWMWA_SYSTEMBACKDROP_TYPE: none */, ref v, sizeof(int));
        v = 1; DwmSetWindowAttribute(hwnd, 2 /* DWMWA_NCRENDERING_POLICY: disabled (no shadow) */, ref v, sizeof(int));
        v = 1; DwmSetWindowAttribute(hwnd, 3 /* DWMWA_TRANSITIONS_FORCEDISABLED */, ref v, sizeof(int));
    }

    /// <summary>The window's on-screen rectangle in device-independent pixels (works when maximized too).</summary>
    public static Rect ScreenRect(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(m.Transform(new Point(r.L, r.T)), m.Transform(new Point(r.R, r.B)));
    }

    /// <summary>A picture of what the window shows right now (null if it has never been laid out).</summary>
    public static BitmapSource? Snapshot(Window w, double quality = 1)
    {
        if (w.Content is not FrameworkElement root || root.ActualWidth < 2 || root.ActualHeight < 2) return null;
        var scale = (PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0) * quality;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bmp.Render(root);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Hides a window from the screen without hiding it from Windows: an empty window region. (DWM cloaking was tried
    /// first, but WPF stops drawing a cloaked window and redraws it all when uncloaked — a ~100 ms stall mid-motion.
    /// With an empty region it keeps drawing normally, so showing it again is instant.)
    /// </summary>
    public static void Cloak(Window w, bool on)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;
        SetWindowRgn(hwnd, on ? CreateRectRgn(0, 0, 0, 0) : IntPtr.Zero, true);
    }

    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr hwnd, IntPtr rgn, bool redraw);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int l, int t, int r, int b);

    /// <summary>Where the morph ends. <paramref name="Reveal"/> runs near the end, when the picture has (almost) reached
    /// its final size: show the real result there; the picture then fades off it, so the last frames are the real thing.</summary>
    public readonly record struct Target(Rect Bounds, ImageSource? Picture, double Radius, Action? Reveal = null, int SettleFrames = 0);

    /// <summary>
    /// Covers <paramref name="from"/> with <paramref name="picture"/>, then calls <paramref name="swap"/> (switch the real
    /// windows; they're hidden under the picture) which says where things end up, animates there, and calls
    /// <paramref name="done"/> (show the real result). The overlay leaves once the result has drawn itself.
    /// </summary>
    public static void Run(Rect area, Rect from, ImageSource picture, double fromRadius, Func<Target?> swap, Action done)
    {
        if (_busy) { swap(); done(); return; }
        Warm();
        _busy = true;
        var overlay = _overlay!;
        var started = Stopwatch.StartNew();
        _a.Source = picture; _a.Opacity = 1;
        _b.Source = null; _b.Opacity = 0;
        Rect to = from;
        double toRadius = fromRadius;
        Action? reveal = null;
        var settleFrames = 0;
        var settleCount = 0;
        var revealed = false;

        void Place(double t)
        {
            var e = Ease(t);
            double L(double x, double y) => x + (y - x) * e;
            Canvas.SetLeft(_box, L(from.Left, to.Left) - area.Left);
            Canvas.SetTop(_box, L(from.Top, to.Top) - area.Top);
            _box.Width = Math.Max(1, L(from.Width, to.Width));
            _box.Height = Math.Max(1, L(from.Height, to.Height));
            _box.CornerRadius = new CornerRadius(L(fromRadius, toRadius));
            if (_b.Source is not null)
            {
                _b.Opacity = Smooth(0.1, 0.6, t);
                _a.Opacity = 1 - Smooth(0.4, 0.9, t);
            }
        }

        Place(0);
        overlay.Left = area.Left; overlay.Top = area.Top; overlay.Width = area.Width; overlay.Height = area.Height;
        overlay.Show();

        // frames: wait until the overlay is on screen, swap underneath, then animate
        var phase = 0;
        var swapped = false;
        var waited = 0;
        var clock = new Stopwatch();
        var last = TimeSpan.MinValue;
        var gaps = new List<double>();
        double prevMs = -1;
        var settle = new Stopwatch();
        double settleLast = 0;
        var quickRun = 0;
        void Frame(object? s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last) return;
            last = now;
            switch (phase)
            {
                case 0: // the overlay needs two frames to be presented
                    if (++waited < 2) return;
                    phase = 1; // swap() can pump frames (showing windows): never run it twice
                    var target = swap();
                    if (target is { } tg) { to = tg.Bounds; toRadius = tg.Radius; _b.Source = tg.Picture; reveal = tg.Reveal; settleFrames = tg.SettleFrames; }
                    swapped = true;
                    waited = 0;
                    return;
                case 1:
                    if (!swapped) return;
                    if (!clock.IsRunning)
                    {
                        // the real windows draw their new state first, and that can take a few slow frames:
                        // hold still until frames come back on time (two quick ones in a row), then move
                        var nowMs = settle.Elapsed.TotalMilliseconds;
                        var quick = settle.IsRunning && nowMs - settleLast < 20;
                        if (!settle.IsRunning) settle.Start();
                        settleLast = nowMs;
                        quickRun = quick ? quickRun + 1 : 0;
                        settleCount++;
                        if ((quickRun < 3 || settleCount < settleFrames) && nowMs < 450) return;
                        clock.Start();
                    }
                    var ms = clock.Elapsed.TotalMilliseconds;
                    if (prevMs >= 0) gaps.Add(ms - prevMs);
                    prevMs = ms;
                    var t = Math.Min(1, ms / Duration.TotalMilliseconds);
                    Place(t);
                    if (t >= 0.7)
                    {
                        if (!revealed) { revealed = true; reveal?.Invoke(); }
                        if (reveal is not null) _box.Opacity = 1 - Smooth(0.72, 1, t);
                    }
                    if (t < 1) return;
                    done();
                    Log.Info($"morph: {gaps.Count} frames in {ms:0} ms (total {started.ElapsedMilliseconds} ms), gaps [{string.Join(",", gaps.Select(g => g.ToString("0")))}]");
                    phase = 2; waited = 0;
                    return;
                default: // let the real window draw itself before the picture goes
                    if (++waited < 3) return;
                    CompositionTarget.Rendering -= Frame;
                    overlay.Hide();
                    _a.Source = null; _b.Source = null; _box.Opacity = 1;
                    _busy = false;
                    return;
            }
        }
        CompositionTarget.Rendering += Frame;
        if (Environment.GetEnvironmentVariable("TUNE_MORPHTRACE") == "1") Trace(overlay.Dispatcher);
    }

    /// <summary>Developer check: logs UI-thread work that takes over 30 ms during the next second.</summary>
    private static void Trace(System.Windows.Threading.Dispatcher d)
    {
        var starts = new Dictionary<System.Windows.Threading.DispatcherOperation, long>();
        var until = Stopwatch.GetTimestamp() + Stopwatch.Frequency;
        void Start(object? s, System.Windows.Threading.DispatcherHookEventArgs e) => starts[e.Operation] = Stopwatch.GetTimestamp();
        void Done(object? s, System.Windows.Threading.DispatcherHookEventArgs e)
        {
            if (!starts.Remove(e.Operation, out var t0)) return;
            var ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            if (ms > 30) Log.Info($"morph trace: {ms:0} ms {e.Operation.Priority} {e.Operation.Task?.ToString()} {DescribeOp(e.Operation)}");
            if (Stopwatch.GetTimestamp() > until) { d.Hooks.OperationStarted -= Start; d.Hooks.OperationCompleted -= Done; }
        }
        d.Hooks.OperationStarted += Start;
        d.Hooks.OperationCompleted += Done;
    }

    private static string DescribeOp(System.Windows.Threading.DispatcherOperation op)
    {
        var f = typeof(System.Windows.Threading.DispatcherOperation).GetField("_method", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        return f?.GetValue(op) is Delegate m ? $"{m.Method.DeclaringType?.Name}.{m.Method.Name} target={m.Target?.GetType().Name}" : "?";
    }

    /// <summary>Ease-out quart: moves off quickly, settles softly.</summary>
    private static double Ease(double t) => 1 - Math.Pow(1 - t, 4);

    /// <summary>The whole monitor a window is on, in device-independent pixels.</summary>
    public static Rect MonitorRect(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info);
        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        return new Rect(m.Transform(new Point(info.Monitor.L, info.Monitor.T)), m.Transform(new Point(info.Monitor.R, info.Monitor.B)));
    }

    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    private static double Smooth(double a, double b, double t)
    {
        var x = Math.Clamp((t - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int L, R, T, B; }
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
}
