using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Tune.App.Shell;
using Tune.Core.Library;

namespace Tune.App.Mini;

public enum WidgetStyle { Wide, Compact, Card, Square }

/// <summary>
/// The floating widget in three styles (compact bar, album-tinted card, art tile): draggable, optionally
/// always on top, remembers where it was left and which style was chosen.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const string PlacementKey = "window.mini";
    private const string StyleKey = "mini.style";
    private readonly LibraryStore _lib;
    private bool _dirty;

    public MiniPlayerWindow(LibraryStore lib)
    {
        _lib = lib;
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            // the wide player is new: everyone sees it once, then their choice sticks
            var style = _lib.GetSetting("mini.wideShown") != "1" ? WidgetStyle.Wide
                : Enum.TryParse<WidgetStyle>(_lib.GetSetting(StyleKey), out var s) ? s : WidgetStyle.Wide;
            _lib.SetSetting("mini.wideShown", "1");
            ApplyStyle(style, save: true);
            RestorePlacement();
        };
        LocationChanged += (_, _) => _dirty = true;
    }

    public WidgetStyle Current { get; private set; }

    public static readonly DependencyProperty QueueProperty = DependencyProperty.Register(nameof(Queue), typeof(Tune.App.ViewModels.QueueViewModel), typeof(MiniPlayerWindow));

    /// <summary>Up next, for the wide style.</summary>
    public Tune.App.ViewModels.QueueViewModel? Queue { get => (Tune.App.ViewModels.QueueViewModel?)GetValue(QueueProperty); set => SetValue(QueueProperty, value); }

    /// <summary>Raised by the wide style lyrics button: open Tune on Now Playing.</summary>
    public event Action? LyricsRequested;

    /// <summary>Raised when the user asks for the main window (⤢).</summary>
    public event Action? ExpandRequested;

    /// <summary>Raised when the user hides the widget (✕), so the app can remember it.</summary>
    public event Action? HideRequested;

    /// <summary>Raised when "always on top" is toggled from the widget's pin.</summary>
    public event Action? PinToggled;

    public void ApplyStyle(WidgetStyle style, bool save = true)
    {
        Current = style;
        WideView.Visibility = style == WidgetStyle.Wide ? Visibility.Visible : Visibility.Collapsed;
        CompactView.Visibility = style == WidgetStyle.Compact ? Visibility.Visible : Visibility.Collapsed;
        CardView.Visibility = style == WidgetStyle.Card ? Visibility.Visible : Visibility.Collapsed;
        SquareView.Visibility = style == WidgetStyle.Square ? Visibility.Visible : Visibility.Collapsed;
        (Width, Height) = style switch
        {
            WidgetStyle.Card => (440.0, 196.0),
            WidgetStyle.Square => (236.0, 236.0),
            WidgetStyle.Wide => (600.0, 206.0),
            _ => (352.0, 96.0),
        };
        if (save) _lib.SetSetting(StyleKey, style.ToString());
        if (IsLoaded) KeepOnScreen();
    }

    private void RestorePlacement()
    {
        var areas = WindowPlacement.CurrentWorkAreas(this);
        var saved = WindowPlacement.Parse(_lib.GetSetting(PlacementKey));
        var r = saved is { } s ? WindowPlacement.Clamp(new Rect(s.X, s.Y, Width, Height), areas) : DefaultSpot(areas);
        (Left, Top) = (r.X, r.Y);
    }

    private void KeepOnScreen()
    {
        var r = WindowPlacement.Clamp(new Rect(Left, Top, Width, Height), WindowPlacement.CurrentWorkAreas(this));
        (Left, Top) = (r.X, r.Y);
    }

    /// <summary>First time: bottom-right corner of the primary screen, above the taskbar.</summary>
    private Rect DefaultSpot(IReadOnlyList<Rect> areas)
    {
        var a = areas.Count > 0 ? areas[0] : SystemParameters.WorkArea;
        return new Rect(a.Right - Width - 16, a.Bottom - Height - 16, Width, Height);
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { TemplatedParent: Button }) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        SavePlacement();
    }

    private void Window_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = this };
        foreach (var style in Enum.GetValues<WidgetStyle>())
        {
            var item = new MenuItem
            {
                Header = style switch { WidgetStyle.Wide => "Wide with queue", WidgetStyle.Card => "Card with controls", WidgetStyle.Square => "Album art tile", _ => "Compact bar" },
                IsCheckable = true,
                IsChecked = style == Current,
            };
            var chosen = style;
            item.Click += (_, _) => ApplyStyle(chosen);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator { Margin = new Thickness(8, 4, 8, 4) });
        var pin = new MenuItem { Header = "Always on top", IsCheckable = true, IsChecked = Topmost };
        pin.Click += (_, _) => PinToggled?.Invoke();
        menu.Items.Add(pin);
        var open = new MenuItem { Header = "Open Tune" };
        open.Click += (_, _) => ExpandRequested?.Invoke();
        menu.Items.Add(open);
        var hide = new MenuItem { Header = "Hide mini-player" };
        hide.Click += (_, _) => HideRequested?.Invoke();
        menu.Items.Add(hide);
        menu.IsOpen = true;
        e.Handled = true;
    }

    public void SavePlacement()
    {
        if (!_dirty) return;
        _dirty = false;
        _lib.SetSetting(PlacementKey, WindowPlacement.Format(new Rect(Left, Top, Width, Height)));
    }

    private void Pin_Click(object sender, RoutedEventArgs e) => PinToggled?.Invoke();

    private void Expand_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();

    private void Lyrics_Click(object sender, RoutedEventArgs e) => LyricsRequested?.Invoke();

    private void QueueRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Tune.App.ViewModels.TrackRowViewModel row) Queue?.JumpCommand.Execute(row);
    }

    private Tune.App.ViewModels.PlayerBarViewModel? Player => DataContext as Tune.App.ViewModels.PlayerBarViewModel;

    private void Seek_Started(object sender, RoutedEventArgs e) => Player?.BeginSeek();

    private void Seek_Completed(object sender, RoutedEventArgs e) => Player?.CommitSeek(WideSeek.Value);
}
