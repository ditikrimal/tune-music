using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Tune.App.Platform;

/// <summary>Windows 11 acrylic (frosted glass) behind a borderless WPF window.</summary>
public static class Backdrop
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1, DwmsbtAcrylic = 3;

    /// <summary>System backdrops arrived in Windows 11 22H2 (build 22621).</summary>
    public static bool Supported => Environment.OSVersion.Version.Build >= 22621;

    public static void Apply(Window w, bool on)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == 0) return;
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        on &= Supported;
        var type = on ? DwmsbtAcrylic : DwmsbtNone;
        _ = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref type, sizeof(int));

        // the backdrop shows only where WPF draws nothing: clear the window and its Direct3D surface
        var source = HwndSource.FromHwnd(hwnd);
        if (source?.CompositionTarget is { } target)
            target.BackgroundColor = on ? Colors.Transparent : Color.FromRgb(0x12, 0x12, 0x12);
        w.Background = on ? Brushes.Transparent : (Brush)Application.Current.Resources["BaseBrush"];
        var margins = on ? new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 } : default;
        _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);
}
