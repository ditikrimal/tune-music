using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Tune.App.ViewModels;

namespace Tune.App.Views;

public partial class NowPlayingView : UserControl
{
    private NowPlayingViewModel? _vm;

    public NowPlayingView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as NowPlayingViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmChanged;
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.CurrentIndex))
            Dispatcher.BeginInvoke(ScrollToCurrent, System.Windows.Threading.DispatcherPriority.Loaded);
        else if (e.PropertyName == nameof(NowPlayingViewModel.HasSynced))
            LyricsScroll.ScrollToTop();
    }

    /// <summary>Keeps the sung line a third of the way down the lyrics panel.</summary>
    private void ScrollToCurrent()
    {
        if (_vm is null || _vm.CurrentIndex < 0 || !IsVisible) return;
        if (LyricsList.ItemContainerGenerator.ContainerFromIndex(_vm.CurrentIndex) is not FrameworkElement line) return;
        var y = line.TransformToAncestor(LyricsScroll).Transform(new Point(0, 0)).Y + LyricsScroll.VerticalOffset;
        var target = Math.Max(0, y - LyricsScroll.ViewportHeight / 3);
        Platform.SmoothScroll.ScrollBy(LyricsScroll, target - LyricsScroll.VerticalOffset);
    }

    /// <summary>Rounded corners for the panel (children are clipped too).</summary>
    private void Panel_SizeChanged(object sender, SizeChangedEventArgs e) =>
        Panel.Clip = new RectangleGeometry(new Rect(e.NewSize), 24, 24);
}
