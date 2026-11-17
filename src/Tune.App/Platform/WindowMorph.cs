using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Tune.App.Platform;

/// <summary>
/// Animates one window turning into another (Tune's window shrinking into the mini-player and back):
/// a borderless see-through overlay shows a picture of the first window, slides and resizes it onto the
/// second window's spot while it crossfades into a picture of the second. The real windows swap underneath.
/// </summary>
public static class WindowMorph
{
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(340);
    private static Window? _running;

    public static bool Busy => _running is not null;

    /// <summary>The window's on-screen rectangle in device-independent pixels (works when maximized too).</summary>
    public static Rect ScreenRect(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return new Rect(w.Left, w.Top, w.ActualWidth, w.ActualHeight);
        var m = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = m.Transform(new Point(r.L, r.T));
        var br = m.Transform(new Point(r.R, r.B));
        return new Rect(tl, br);
    }

    /// <summary>A picture of what the window shows right now (null if it has never been laid out).</summary>
    public static BitmapSource? Snapshot(Window w)
    {
        if (w.Content is not FrameworkElement root || root.ActualWidth < 2 || root.ActualHeight < 2) return null;
        var scale = PresentationSource.FromVisual(w)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * scale), (int)Math.Ceiling(root.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        // render through a brush so the root's own margin/offset doesn't shift the picture
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        bmp.Render(dv);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>
    /// Plays the morph from <paramref name="from"/> to <paramref name="to"/>; <paramref name="swap"/> runs once the overlay
    /// covers the screen (hide the old window there), <paramref name="done"/> when the motion ends (show the new one).
    /// </summary>
    public static void Run(Rect from, ImageSource fromPicture, double fromRadius, Rect to, ImageSource? toPicture, double toRadius,
                           Action swap, Action done)
    {
        _running?.Close();
        var area = Rect.Union(from, to);
        area.Inflate(2, 2);
        var box = new Border { ClipToBounds = true, CornerRadius = new CornerRadius(fromRadius) };
        var grid = new Grid();
        var a = new Image { Source = fromPicture, Stretch = Stretch.Fill };
        var b = toPicture is null ? null : new Image { Source = toPicture, Stretch = Stretch.Fill, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(a, BitmapScalingMode.Linear);
        grid.Children.Add(a);
        if (b is not null) { RenderOptions.SetBitmapScalingMode(b, BitmapScalingMode.Linear); grid.Children.Add(b); }
        box.Child = grid;
        var canvas = new Canvas();
        canvas.Children.Add(box);
        var overlay = new Window
        {
            WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
            Left = area.Left, Top = area.Top, Width = area.Width, Height = area.Height, Content = canvas, IsHitTestVisible = false,
        };
        _running = overlay;

        void Place(double t)
        {
            var e = 1 - Math.Pow(1 - t, 3); // ease out: fast start, soft landing
            double L(double x, double y) => x + (y - x) * e;
            var r = new Rect(L(from.Left, to.Left) - area.Left, L(from.Top, to.Top) - area.Top, L(from.Width, to.Width), L(from.Height, to.Height));
            Canvas.SetLeft(box, r.Left); Canvas.SetTop(box, r.Top);
            box.Width = Math.Max(1, r.Width); box.Height = Math.Max(1, r.Height);
            box.CornerRadius = new CornerRadius(L(fromRadius, toRadius));
            if (b is not null)
            {
                b.Opacity = Smooth(0.15, 0.65, t);
                a.Opacity = 1 - Smooth(0.35, 0.85, t);
            }
        }

        Place(0);
        var clock = new Stopwatch();
        var last = TimeSpan.MinValue;
        void Frame(object? s, EventArgs e)
        {
            var now = ((RenderingEventArgs)e).RenderingTime;
            if (now == last) return;
            last = now;
            if (!clock.IsRunning) { clock.Start(); swap(); }
            var t = Math.Min(1, clock.Elapsed.TotalMilliseconds / Duration.TotalMilliseconds);
            Place(t);
            if (t < 1) return;
            CompositionTarget.Rendering -= Frame;
            done();
            // keep the last picture up until the real window has drawn itself, then let go
            overlay.Dispatcher.BeginInvoke(() =>
            {
                overlay.Close();
                if (_running == overlay) _running = null;
            }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        }
        overlay.ContentRendered += (_, _) => CompositionTarget.Rendering += Frame;
        overlay.Show();
    }

    private static double Smooth(double a, double b, double t)
    {
        var x = Math.Clamp((t - a) / (b - a), 0, 1);
        return x * x * (3 - 2 * x);
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
}
