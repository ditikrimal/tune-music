using System.Windows;
using Tune.App.Shell;

namespace Tune.App.Tests;

public class WindowPlacementTests
{
    private static readonly Rect Primary = new(0, 0, 1920, 1040);
    private static readonly Rect Right = new(1920, 0, 2560, 1400);

    [Fact]
    public void ClampToWorkArea_keeps_visible_window_unchanged()
    {
        var r = new Rect(100, 100, 1280, 800);
        Assert.Equal(r, WindowPlacement.Clamp(r, [Primary]));
        var onSecond = new Rect(2200, 300, 1280, 800);
        Assert.Equal(onSecond, WindowPlacement.Clamp(onSecond, [Primary, Right]));
    }

    [Fact]
    public void ClampToWorkArea_moves_window_from_missing_monitor_onto_primary()
    {
        // saved on the second screen that MonitorSwapper turned off
        var r = WindowPlacement.Clamp(new Rect(2200, 300, 1280, 800), [Primary]);
        Assert.True(Primary.Contains(r), r.ToString());
        Assert.Equal(new Size(1280, 800), r.Size);
    }

    [Fact]
    public void ClampToWorkArea_shrinks_window_larger_than_screen()
    {
        var r = WindowPlacement.Clamp(new Rect(5000, 5000, 3000, 2000), [Primary]);
        Assert.True(Primary.Contains(r), r.ToString());
        Assert.Equal(Primary.Size, r.Size);
    }

    [Fact]
    public void ClampToWorkArea_nudges_window_with_title_bar_off_screen()
    {
        // only 20 px of the window peeks in at the bottom, title bar unreachable
        var r = WindowPlacement.Clamp(new Rect(100, 1020, 800, 600), [Primary]);
        Assert.True(Primary.Contains(new Rect(r.X, r.Y, r.Width, 48)), r.ToString());
    }

    [Fact]
    public void ClampToWorkArea_small_mini_player_in_corner_stays()
    {
        var mini = new Rect(1600, 960, 300, 64);
        Assert.Equal(mini, WindowPlacement.Clamp(mini, [Primary]));
    }

    [Fact]
    public void Parse_and_format_roundtrip_and_bad_input()
    {
        var r = new Rect(10.5, 20, 300, 64);
        Assert.Equal(r, WindowPlacement.Parse(WindowPlacement.Format(r)));
        Assert.Null(WindowPlacement.Parse("garbage"));
        Assert.Null(WindowPlacement.Parse(null));
    }
}
