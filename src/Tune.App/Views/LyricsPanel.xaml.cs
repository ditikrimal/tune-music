using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Tune.App.ViewModels;

namespace Tune.App.Views;

/// <summary>The lyrics column (Now Playing and full screen): keeps the sung line a third of the way down.</summary>
public partial class LyricsPanel : UserControl
{
    public static readonly DependencyProperty LineSizeProperty = DependencyProperty.Register(nameof(LineSize), typeof(double), typeof(LyricsPanel), new PropertyMetadata(26.0));
    public static readonly DependencyProperty LineSpacingProperty = DependencyProperty.Register(nameof(LineSpacing), typeof(Thickness), typeof(LyricsPanel), new PropertyMetadata(new Thickness(0, 9, 0, 9)));
    public static readonly DependencyProperty PlainSizeProperty = DependencyProperty.Register(nameof(PlainSize), typeof(double), typeof(LyricsPanel), new PropertyMetadata(24.0));
    public static readonly DependencyProperty PlainLineHeightProperty = DependencyProperty.Register(nameof(PlainLineHeight), typeof(double), typeof(LyricsPanel), new PropertyMetadata(40.0));

    public double LineSize { get => (double)GetValue(LineSizeProperty); set => SetValue(LineSizeProperty, value); }
    public Thickness LineSpacing { get => (Thickness)GetValue(LineSpacingProperty); set => SetValue(LineSpacingProperty, value); }
    public double PlainSize { get => (double)GetValue(PlainSizeProperty); set => SetValue(PlainSizeProperty, value); }
    public double PlainLineHeight { get => (double)GetValue(PlainLineHeightProperty); set => SetValue(PlainLineHeightProperty, value); }

    private NowPlayingViewModel? _vm;

    public LyricsPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as NowPlayingViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
        // shown again (e.g. full screen opened): jump to the line being sung
        // appearing: jump straight to the line (a glide from the top would run during the window's own transition)
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Dispatcher.BeginInvoke(() => ScrollToCurrent(instant: true), System.Windows.Threading.DispatcherPriority.Loaded); };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.CurrentIndex))
            Dispatcher.BeginInvoke(() => ScrollToCurrent(), System.Windows.Threading.DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(NowPlayingViewModel.HasSynced))
            LyricsScroll.ScrollToTop();
    }

    private void ScrollToCurrent() => ScrollToCurrent(instant: false);

    /// <summary>Puts the current line in place right now (used before a transition, so nothing moves during it).</summary>
    public void JumpToCurrent() => ScrollToCurrent(instant: true);

    private void ScrollToCurrent(bool instant)
    {
        if (_vm is null || _vm.CurrentIndex < 0 || !IsVisible) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(_vm.CurrentIndex) is not FrameworkElement line) return;
        var y = line.TransformToAncestor(LyricsScroll).Transform(new Point(0, 0)).Y + LyricsScroll.VerticalOffset;
        var target = Math.Max(0, y - LyricsScroll.ViewportHeight / 3);
        if (instant) LyricsScroll.ScrollToVerticalOffset(target);
        else Platform.SmoothScroll.ScrollBy(LyricsScroll, target - LyricsScroll.VerticalOffset);
    }
}
