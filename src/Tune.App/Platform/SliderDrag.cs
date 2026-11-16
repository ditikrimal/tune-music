using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Tune.App.Platform;

/// <summary>
/// Press anywhere on a slider and drag: the value follows the pointer from the first click, with no jump back.
/// WPF's own IsMoveToPointEnabled swallows the click (so "seek started" never fires) and doesn't drag
/// unless you grab the thumb exactly. Raises <see cref="DragStartedEvent"/> / <see cref="DragCompletedEvent"/>.
/// </summary>
public static class SliderDrag
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(SliderDrag), new PropertyMetadata(false, OnEnabledChanged));

    public static bool GetEnabled(DependencyObject d) => (bool)d.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject d, bool v) => d.SetValue(EnabledProperty, v);

    public static readonly RoutedEvent DragStartedEvent = EventManager.RegisterRoutedEvent(
        "DragStarted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SliderDrag));

    public static readonly RoutedEvent DragCompletedEvent = EventManager.RegisterRoutedEvent(
        "DragCompleted", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(SliderDrag));

    public static void AddDragStartedHandler(DependencyObject d, RoutedEventHandler h) => (d as UIElement)?.AddHandler(DragStartedEvent, h);
    public static void RemoveDragStartedHandler(DependencyObject d, RoutedEventHandler h) => (d as UIElement)?.RemoveHandler(DragStartedEvent, h);
    public static void AddDragCompletedHandler(DependencyObject d, RoutedEventHandler h) => (d as UIElement)?.AddHandler(DragCompletedEvent, h);
    public static void RemoveDragCompletedHandler(DependencyObject d, RoutedEventHandler h) => (d as UIElement)?.RemoveHandler(DragCompletedEvent, h);

    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Slider s) return;
        if ((bool)e.NewValue)
        {
            s.PreviewMouseLeftButtonDown += Down;
            s.PreviewMouseMove += Move;
            s.PreviewMouseLeftButtonUp += Up;
            s.LostMouseCapture += Lost;
        }
        else
        {
            s.PreviewMouseLeftButtonDown -= Down;
            s.PreviewMouseMove -= Move;
            s.PreviewMouseLeftButtonUp -= Up;
            s.LostMouseCapture -= Lost;
        }
    }

    private static void Down(object sender, MouseButtonEventArgs e)
    {
        var s = (Slider)sender;
        if (!s.IsEnabled) return;
        e.Handled = true;
        s.RaiseEvent(new RoutedEventArgs(DragStartedEvent, s));
        s.CaptureMouse();
        Follow(s, e);
    }

    private static void Move(object sender, MouseEventArgs e)
    {
        var s = (Slider)sender;
        if (!s.IsMouseCaptured) return;
        e.Handled = true;
        Follow(s, e);
    }

    private static void Up(object sender, MouseButtonEventArgs e)
    {
        var s = (Slider)sender;
        if (!s.IsMouseCaptured) return;
        e.Handled = true;
        Follow(s, e);
        s.ReleaseMouseCapture(); // → Lost → completed
    }

    private static void Lost(object sender, MouseEventArgs e) =>
        ((Slider)sender).RaiseEvent(new RoutedEventArgs(DragCompletedEvent, sender));

    private static void Follow(Slider s, MouseEventArgs e)
    {
        if (s.Template.FindName("PART_Track", s) is not Track track || track.ActualWidth <= 0) return;
        // straight from where the pointer is on the bar (not relative to the thumb, which may be mid-move:
        // Track.ValueFromPoint made the value flash to 0 or 100 % for a frame)
        var thumb = track.Thumb?.ActualWidth ?? 0;
        var usable = track.ActualWidth - thumb;
        var x = e.GetPosition(track).X;
        var fraction = usable > 1 ? (x - thumb / 2) / usable : x / track.ActualWidth;
        var v = s.Minimum + Math.Clamp(fraction, 0, 1) * (s.Maximum - s.Minimum);
        s.SetCurrentValue(RangeBase.ValueProperty, v); // keeps a OneWay binding alive
    }
}
