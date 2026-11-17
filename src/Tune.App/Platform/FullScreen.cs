using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;

namespace Tune.App.Platform;

/// <summary>Makes a WindowChrome window cover its whole monitor (taskbar included) and puts it back exactly as it was.</summary>
public sealed class FullScreen(Window window)
{
    private Rect _bounds;
    private WindowState _state;
    private double _caption;
    private Thickness _resize;

    public bool IsOn { get; private set; }

    public void Enter()
    {
        if (IsOn) return;
        IsOn = true;
        _state = window.WindowState;
        _bounds = _state == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds;
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            _caption = chrome.CaptionHeight;
            _resize = chrome.ResizeBorderThickness;
            chrome.CaptionHeight = 0;                    // nothing to drag in full screen
            chrome.ResizeBorderThickness = new Thickness(0);
        }
        var monitor = MonitorRect();
        window.WindowState = WindowState.Normal;
        window.Topmost = true;  // above the taskbar
        Place(monitor);
        window.Activate();
    }

    public void Exit()
    {
        if (!IsOn) return;
        IsOn = false;
        window.Topmost = false;
        if (WindowChrome.GetWindowChrome(window) is { } chrome)
        {
            chrome.CaptionHeight = _caption;
            chrome.ResizeBorderThickness = _resize;
        }
        Place(_bounds);
        if (_state == WindowState.Maximized) window.WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// Moves and sizes the window in one step. Setting Left/Top/Width/Height one by one resizes the window (and its
    /// Direct3D surface) several times, each ~100 ms of rendering for a full-screen window.
    /// </summary>
    private void Place(Rect r)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var dpi = VisualTreeHelperDpi();
        SetWindowPos(hwnd, 0, (int)Math.Round(r.Left * dpi), (int)Math.Round(r.Top * dpi), (int)Math.Round(r.Width * dpi), (int)Math.Round(r.Height * dpi),
            0x0004 /* NOZORDER */ | 0x0010 /* NOACTIVATE */);
    }

    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);

    /// <summary>The monitor the window is on, in WPF units.</summary>
    private Rect MonitorRect()
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(MonitorFromWindow(hwnd, 2 /* nearest */), ref info);
        var dpi = VisualTreeHelperDpi();
        return new Rect(info.Monitor.Left / dpi, info.Monitor.Top / dpi,
            (info.Monitor.Right - info.Monitor.Left) / dpi, (info.Monitor.Bottom - info.Monitor.Top) / dpi);
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(window).DpiScaleX;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rc { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Rc Monitor; public Rc Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
