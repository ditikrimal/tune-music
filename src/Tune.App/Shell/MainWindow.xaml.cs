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
            if (DataContext is ShellViewModel vm)
                vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ShellViewModel.Page)) AnimatePageIn(); };
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
