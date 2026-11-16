using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Tune.App.Platform;

/// <summary>
/// Browser-style smooth wheel scrolling for every ScrollViewer in the app: each wheel notch moves a target,
/// and the view eases towards it once per displayed frame. The innermost viewer that can scroll that way gets
/// the wheel, so horizontal shelves pass vertical wheel movement to the page.
/// While a viewer moves, its content ignores the mouse (no hover highlights, card lifts or tooltips flickering
/// past the pointer — each of those costs a redraw mid-scroll); hover comes back a moment after it stops.
/// </summary>
public static class SmoothScroll
{
    private const double PixelsPerNotch = 120;     // one wheel notch
    private const double SmoothingSeconds = 0.09;  // time constant of the ease-out
    private static readonly TimeSpan HoverBackAfter = TimeSpan.FromMilliseconds(140);

    private sealed class State
    {
        public double Target;
        public bool Animating;
        public bool Horizontal;
        public TimeSpan Last;
        public UIElement? Muted;
        public DispatcherTimer? HoverTimer;
    }

    private static readonly ConditionalWeakTable<ScrollViewer, State> States = new();
    private static readonly List<ScrollViewer> Active = [];

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnPreviewMouseWheel));

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers != ModifierKeys.None) return;
        var horizontal = false;
        var sv = FindScrollable(e.OriginalSource as DependencyObject, e.Delta, ref horizontal);
        if (sv is null) return;
        e.Handled = true;
        ScrollBy(sv, -e.Delta / 120.0 * PixelsPerNotch, horizontal);
    }

    /// <summary>Animates a viewer by <paramref name="pixels"/> (used by the shelf arrow buttons too).</summary>
    public static void ScrollBy(ScrollViewer sv, double pixels, bool horizontal = false)
    {
        var state = States.GetValue(sv, _ => new State());
        var current = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
        var max = horizontal ? sv.ScrollableWidth : sv.ScrollableHeight;
        var from = state.Animating ? state.Target : current;
        state.Target = Math.Clamp(from + pixels, 0, max);
        state.Horizontal = horizontal;
        MuteHover(sv, state);
        if (state.Animating) return;
        state.Animating = true;
        state.Last = TimeSpan.Zero;
        if (Active.Count == 0) CompositionTarget.Rendering += OnFrame;
        Active.Add(sv);
    }

    private static void OnFrame(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        for (var i = Active.Count - 1; i >= 0; i--)
        {
            var sv = Active[i];
            if (!States.TryGetValue(sv, out var s)) { Active.RemoveAt(i); continue; }
            // WPF can raise Rendering more than once per displayed frame: step once per frame, or motion judders
            if (now == s.Last) continue;
            var dt = s.Last == TimeSpan.Zero ? 1 / 60.0 : Math.Min((now - s.Last).TotalSeconds, 0.05);
            s.Last = now;
            var horizontal = s.Horizontal;
            var current = horizontal ? sv.HorizontalOffset : sv.VerticalOffset;
            var diff = s.Target - current;
            if (Math.Abs(diff) < 0.5 || !sv.IsLoaded)
            {
                Set(sv, s.Target, horizontal);
                s.Animating = false;
                Active.RemoveAt(i);
                RestoreHoverSoon(s);
                continue;
            }
            Set(sv, current + diff * (1 - Math.Exp(-dt / SmoothingSeconds)), horizontal);
        }
        if (Active.Count == 0) CompositionTarget.Rendering -= OnFrame;
    }

    private static void MuteHover(ScrollViewer sv, State s)
    {
        s.HoverTimer?.Stop();
        if (s.Muted is not null || sv.Content is not UIElement content) return;
        // the viewer itself keeps receiving the wheel while its content ignores the mouse
        if (sv.Background is null) sv.Background = Brushes.Transparent;
        content.IsHitTestVisible = false;
        s.Muted = content;
    }

    private static void RestoreHoverSoon(State s)
    {
        if (s.Muted is null) return;
        s.HoverTimer ??= new DispatcherTimer(DispatcherPriority.Input) { Interval = HoverBackAfter, Tag = s };
        s.HoverTimer.Tick -= OnHoverTimer;
        s.HoverTimer.Tick += OnHoverTimer;
        s.HoverTimer.Start();
    }

    private static void OnHoverTimer(object? sender, EventArgs e)
    {
        if (sender is not DispatcherTimer { Tag: State s } timer) return;
        timer.Stop();
        if (s.Animating || s.Muted is null) return;
        s.Muted.IsHitTestVisible = true;
        s.Muted = null;
    }

    private static void Set(ScrollViewer sv, double offset, bool horizontal)
    {
        if (horizontal) sv.ScrollToHorizontalOffset(offset);
        else sv.ScrollToVerticalOffset(offset);
    }

    /// <summary>Innermost ancestor viewer that can still move in the wheel's direction.</summary>
    private static ScrollViewer? FindScrollable(DependencyObject? d, int delta, ref bool horizontal)
    {
        for (; d is not null; d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is not ScrollViewer sv) continue;
            if (sv.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled && sv.ScrollableHeight > 0
                && (delta > 0 ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight))
            {
                horizontal = false;
                return sv;
            }
        }
        return null;
    }
}
