using System.ComponentModel;
using System.Windows.Controls;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Tune.App.ViewModels;

namespace Tune.App.Platform;

/// <summary>Tray icon: left-click shows Tune; right-click menu for mini-player, play/pause, next, quit.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly ShellViewModel _shell;
    private readonly MenuItem _playPause;
    private readonly MenuItem _mini;

    public TrayIcon(ShellViewModel shell, Action showMain, Action toggleMini, Func<bool> miniVisible, Action quit)
    {
        _shell = shell;
        _playPause = Item("Play", () => shell.Player.PlayPauseCommand.Execute(null));
        _mini = new MenuItem { Header = "Mini-player", IsCheckable = true };
        _mini.Click += (_, _) => toggleMini();

        var menu = new ContextMenu();
        menu.Items.Add(Item("Show Tune", showMain));
        menu.Items.Add(_mini);
        menu.Items.Add(new Separator { Background = System.Windows.Media.Brushes.DimGray, Margin = new System.Windows.Thickness(8, 4, 8, 4) });
        menu.Items.Add(_playPause);
        menu.Items.Add(Item("Next", () => shell.Player.NextCommand.Execute(null)));
        menu.Items.Add(new Separator { Background = System.Windows.Media.Brushes.DimGray, Margin = new System.Windows.Thickness(8, 4, 8, 4) });
        menu.Items.Add(Item("Quit", quit));
        menu.Opened += (_, _) =>
        {
            _mini.IsChecked = miniVisible();
            _playPause.Header = shell.Player.IsPlaying ? "Pause" : "Play";
        };

        _icon = new TaskbarIcon
        {
            IconSource = AppIcon.Create(32),
            ToolTipText = "Tune",
            ContextMenu = menu,
            MenuActivation = PopupActivationMode.RightClick,
            NoLeftClickDelay = true,
        };
        _icon.TrayLeftMouseUp += (_, _) => showMain();
        _icon.ForceCreate(enablesEfficiencyMode: false);

        shell.Player.PropertyChanged += OnPlayerChanged;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(PlayerBarViewModel.Track))) return;
        var p = _shell.Player;
        var text = p.HasTrack ? $"{p.Title} — {p.ArtistLine}" : "Tune";
        _icon.ToolTipText = text.Length > 120 ? text[..119] + "…" : text; // tray tooltips are capped at 128 chars
    }

    public void ShowHint(string title, string message) =>
        _icon.ShowNotification(title, message, NotificationIcon.Info);

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    public void Dispose()
    {
        _shell.Player.PropertyChanged -= OnPlayerChanged;
        _icon.Dispose();
    }
}
