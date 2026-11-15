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
