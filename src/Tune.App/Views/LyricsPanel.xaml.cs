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
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Dispatcher.BeginInvoke(ScrollToCurrent, System.Windows.Threading.DispatcherPriority.Loaded); };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.CurrentIndex))
            Dispatcher.BeginInvoke(ScrollToCurrent, System.Windows.Threading.DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(NowPlayingViewModel.HasSynced))
            LyricsScroll.ScrollToTop();
    }

    private void ScrollToCurrent()
    {
        if (_vm is null || _vm.CurrentIndex < 0 || !IsVisible) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(_vm.CurrentIndex) is not FrameworkElement line) return;
        var y = line.TransformToAncestor(LyricsScroll).Transform(new Point(0, 0)).Y + LyricsScroll.VerticalOffset;
        var target = Math.Max(0, y - LyricsScroll.ViewportHeight / 3);
        Platform.SmoothScroll.ScrollBy(LyricsScroll, target - LyricsScroll.VerticalOffset);
    }
}
