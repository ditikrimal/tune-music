using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace Tune.App.Shell;

/// <summary>Saves/restores window bounds, keeping them on a screen that still exists.</summary>
public static class WindowPlacement
{
    private const double TitleBarHeight = 48;
    private const double MinVisible = 50;

    /// <summary>
    /// Returns bounds that are reachable on one of <paramref name="workAreas"/>: unchanged when the title bar
    /// is comfortably visible, nudged in when mostly off-screen, moved onto the first (primary) area when on no screen.
    /// </summary>
    public static Rect Clamp(Rect saved, IReadOnlyList<Rect> workAreas)
    {
        if (workAreas.Count == 0) return saved;
        var titleBar = new Rect(saved.X, saved.Y, saved.Width, Math.Min(TitleBarHeight, saved.Height));

        foreach (var area in workAreas)
        {
            var visible = Rect.Intersect(area, titleBar);
            if (!visible.IsEmpty && visible.Width >= Math.Min(MinVisible, saved.Width) && visible.Height >= Math.Min(TitleBarHeight, saved.Height) - 0.5)
                return saved;
        }

        // choose the area that overlaps most, else the primary one
        var target = workAreas
            .Select(a => (Area: a, Overlap: Rect.Intersect(a, saved) is { IsEmpty: false } r ? r.Width * r.Height : 0))
            .OrderByDescending(x => x.Overlap)
            .First();
        var screen = target.Overlap > 0 ? target.Area : workAreas[0];

        var w = Math.Min(saved.Width, screen.Width);
        var h = Math.Min(saved.Height, screen.Height);
        var x = Math.Clamp(saved.X, screen.Left, screen.Right - w);
        var y = Math.Clamp(saved.Y, screen.Top, screen.Bottom - h);
        return new Rect(x, y, w, h);
    }

    public static string Format(Rect r) =>
        string.Join(',', new[] { r.X, r.Y, r.Width, r.Height }.Select(v => v.ToString("0.##", CultureInfo.InvariantCulture)));

    public static Rect? Parse(string? s)
    {
        var parts = s?.Split(',');
        if (parts is not { Length: 4 }) return null;
        var v = new double[4];
        for (var i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out v[i])) return null;
        return v[2] > 0 && v[3] > 0 ? new Rect(v[0], v[1], v[2], v[3]) : null;
    }

    /// <summary>Work areas of all monitors (primary first) in WPF device-independent units.</summary>
    public static IReadOnlyList<Rect> CurrentWorkAreas(Visual reference)
    {
        var scale = VisualTreeHelper.GetDpi(reference).DpiScaleX;
        if (scale <= 0) scale = 1;
        var areas = new List<(Rect Area, bool Primary)>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                var w = info.Work;
                areas.Add((new Rect(w.Left / scale, w.Top / scale, (w.Right - w.Left) / scale, (w.Bottom - w.Top) / scale), (info.Flags & 1) != 0));
            }
            return true;
        }, 0);
        return areas.OrderByDescending(a => a.Primary).Select(a => a.Area).ToList();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Win32Rect Monitor; public Win32Rect Work; public uint Flags; }

    private delegate bool MonitorEnumProc(nint monitor, nint hdc, nint rect, nint data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(nint hdc, nint clip, MonitorEnumProc proc, nint data);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
}
