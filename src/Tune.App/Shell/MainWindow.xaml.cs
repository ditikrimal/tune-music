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
        SourceInitialized += (_, _) => UseRoundedCorners();
    }

    private ShellViewModel Vm => (ShellViewModel)DataContext;

    /// <summary>Raised by the mini-player button; the app wires it to the floating mini-player.</summary>
    public event Action? MiniPlayerRequested;

    private void OnWindowStateChanged()
    {
        // a WindowChrome window overhangs the screen by the resize border when maximized
        Root.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        Vm.Player.UpdatesEnabled = WindowState != WindowState.Minimized || MiniVisible;
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

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        App.Quit();
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
