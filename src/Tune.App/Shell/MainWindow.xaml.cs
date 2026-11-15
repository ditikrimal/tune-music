using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Tune.App.ViewModels;

namespace Tune.App.Shell;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => OnWindowStateChanged();
        // the page header fades to the playing song's colour (or the theme's)
        Tune.App.Platform.ThemeManager.HeaderChanged += c => Dispatcher.BeginInvoke(() =>
            HeaderStop.BeginAnimation(System.Windows.Media.GradientStop.ColorProperty,
                new System.Windows.Media.Animation.ColorAnimation(c, TimeSpan.FromMilliseconds(700))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut },
                }));
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not ShellViewModel vm) return;
            vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.Page)) AnimatePageIn(); };
            vm.Player.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(PlayerBarViewModel.IsPlaying)) UpdateSpin(vm.Player.IsPlaying);
            };
            Loaded += (_, _) => UpdateSpin(vm.Player.IsPlaying);
        };
                SourceInitialized += (_, _) =>
        {
            UseRoundedCorners();
            RestorePlacement();
        };
    }

    private const string PlacementKey = "window.main";

    private void RestorePlacement()
    {
        var lib = Vm.Services.Library;
        if (WindowPlacement.Parse(lib.GetSetting(PlacementKey)) is not { } saved) return;
        var r = WindowPlacement.Clamp(saved, WindowPlacement.CurrentWorkAreas(this));
        WindowStartupLocation = WindowStartupLocation.Manual;
        (Left, Top, Width, Height) = (r.X, r.Y, r.Width, r.Height);
        if (lib.GetSetting(PlacementKey + ".max") == "1") WindowState = WindowState.Maximized;
    }

    public void SavePlacement()
    {
        var lib = Vm.Services.Library;
        lib.SetSetting(PlacementKey, WindowPlacement.Format(RestoreBounds.IsEmpty ? new Rect(Left, Top, Width, Height) : RestoreBounds));
        lib.SetSetting(PlacementKey + ".max", WindowState == WindowState.Maximized ? "1" : "0");
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SavePlacement();
        if (!App.IsQuitting && Vm.Services.Library.GetSetting("window.closeToTray") == "0")
        {
            App.Quit();
            return;
        }
        if (!App.IsQuitting)
        {
            // close = hide to tray; the music keeps playing
            e.Cancel = true;
            Hide();
            App.OnMainClosedToTray();
        }
        base.OnClosing(e);
    }

    private ShellViewModel Vm => (ShellViewModel)DataContext;

    /// <summary>Keyboard shortcuts (Spotify-like). Space is left alone while typing in a text box.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var alt = Keyboard.Modifiers.HasFlag(ModifierKeys.Alt);
        var typing = Keyboard.FocusedElement is System.Windows.Controls.TextBox;
        var p = Vm.Player;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.K or Key.F when ctrl:
                TopSearch.Focus();
                TopSearch.SelectAll();
                break;
            case Key.Space when !typing:
                p.PlayPauseCommand.Execute(null);
                break;
            case Key.Right when ctrl:
                p.NextCommand.Execute(null);
                break;
            case Key.Left when ctrl:
                p.PreviousCommand.Execute(null);
                break;
            case Key.Up when ctrl:
                p.Volume = Math.Min(1, p.Volume + 0.05);
                break;
            case Key.Down when ctrl:
                p.Volume = Math.Max(0, p.Volume - 0.05);
                break;
            case Key.L when ctrl:
                p.ToggleLikeCommand.Execute(null);
                break;
            case Key.Left when alt:
                Vm.GoBackCommand.Execute(null);
                break;
            case Key.Right when alt:
                Vm.GoForwardCommand.Execute(null);
                break;
            case Key.Escape when typing:
                Keyboard.ClearFocus();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private System.Windows.Media.Animation.AnimationClock? _spin;

    /// <summary>The now-playing disc turns slowly while music plays and stops where it is on pause.</summary>
    private void UpdateSpin(bool playing)
    {
        if (_spin is null)
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, 360, TimeSpan.FromSeconds(14))
            {
                RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever,
            };
            _spin = anim.CreateClock();
            DiscSpin.ApplyAnimationClock(System.Windows.Media.RotateTransform.AngleProperty, _spin);
        }
        if (playing) _spin.Controller?.Resume();
        else _spin.Controller?.Pause();
    }

    /// <summary>New page fades in while sliding up a little.</summary>
    private void AnimatePageIn()
    {
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        PageHost.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        PageSlide.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    /// <summary>Raised by the mini-player button; the app wires it to the floating mini-player.</summary>
    public event Action? MiniPlayerRequested;

    private void OnWindowStateChanged()
    {
        // a WindowChrome window overhangs the screen by the resize border when maximized
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        App.SyncUpdates();
    }

    /// <summary>Set by the mini-player so position updates keep flowing while the main window is minimised.</summary>
    public bool MiniVisible { get; set; }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Mini_Click(object sender, RoutedEventArgs e) => MiniPlayerRequested?.Invoke();

    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => Vm.Player.BeginSeek();

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e) => Vm.Player.CommitSeek(SeekSlider.Value);

    private void NewPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var lib = Vm.Services.Library;
        var id = lib.CreatePlaylist($"My playlist #{lib.GetPlaylists().Count + 1}");
        Vm.ReloadPlaylists();
        Vm.GoToCommand.Execute($"playlist:{id}");
    }

    // Windows 11 rounded window corners for our borderless chrome
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private void UseRoundedCorners()
    {
        const int DwmwaWindowCornerPreference = 33, DwmwcpRound = 2;
        var pref = DwmwcpRound;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DwmwaWindowCornerPreference, ref pref, sizeof(int));
    }
}
