using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Tune.App.ViewModels;

namespace Tune.App.Views;

/// <summary>
/// Full-screen Now Playing. The controls, top bar and pointer fade away after a few still seconds while music plays,
/// and come back on any mouse movement.
/// </summary>
public partial class ImmersiveView : UserControl
{
    private static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(3);
    private readonly DispatcherTimer _idle;
    private NowPlayingViewModel? _vm;
    private bool _hidden;
    private Point _lastMouse;

    public ImmersiveView()
    {
        InitializeComponent();
        _idle = new DispatcherTimer { Interval = HideAfter };
        _idle.Tick += (_, _) => { _idle.Stop(); if (CanHide()) SetHidden(true); };
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.PropertyChanged -= OnVmChanged;
            _vm = DataContext as NowPlayingViewModel;
            if (_vm is not null) { _vm.PropertyChanged += OnVmChanged; LayoutForLyrics(); }
        };
        IsVisibleChanged += (_, e) =>
        {
            if ((bool)e.NewValue) { SetHidden(false); _idle.Start(); }
            else { _idle.Stop(); SetHidden(false); }
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NowPlayingViewModel.ShowLyrics)) LayoutForLyrics();
    }

    /// <summary>Lyrics off: the cover moves to the middle of the screen.</summary>
    private void LayoutForLyrics()
    {
        var show = _vm?.ShowLyrics != false;
        LyricsColumn.Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private bool CanHide() => IsVisible && _vm?.Player.IsPlaying == true && !Seek.IsMouseCaptureWithin && !Controls.IsMouseOver && !TopBar.IsMouseOver;

    private void OnActivity(object sender, MouseEventArgs e)
    {
        // WPF sends MouseMove when content changes under a still pointer: only real movement counts
        var p = e.GetPosition(this);
        if (e.RoutedEvent == MouseMoveEvent && (p - _lastMouse).Length < 2) return;
        _lastMouse = p;
        SetHidden(false);
        _idle.Stop();
        _idle.Start();
    }

    private void SetHidden(bool hidden)
    {
        if (_hidden == hidden) return;
        _hidden = hidden;
        var to = hidden ? 0.0 : 1.0;
        var fade = new DoubleAnimation(to, TimeSpan.FromMilliseconds(hidden ? 600 : 180)) { EasingFunction = new CubicEase() };
        Controls.BeginAnimation(OpacityProperty, fade);
        TopBar.BeginAnimation(OpacityProperty, fade);
        Cursor = hidden ? Cursors.None : null;
    }

    private void Seek_Started(object sender, RoutedEventArgs e) => _vm?.Player.BeginSeek();

    private void Seek_Completed(object sender, RoutedEventArgs e) => _vm?.Player.CommitSeek(Seek.Value);

    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_vm?.Player.IsSeeking == true) _vm.Player.PreviewSeek(e.NewValue);
    }

    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm is null) return;
        _vm.Player.Volume = Math.Clamp(_vm.Player.Volume + Math.Sign(e.Delta) * 0.05, 0, 1);
        e.Handled = true;
    }
}
