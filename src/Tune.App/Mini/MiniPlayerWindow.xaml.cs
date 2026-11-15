using System.Windows;
using System.Windows.Input;
using Tune.App.Shell;
using Tune.Core.Library;

namespace Tune.App.Mini;

/// <summary>The floating "slim bar": always on top, draggable, remembers where it was left.</summary>
public partial class MiniPlayerWindow : Window
{
    private const string PlacementKey = "window.mini";
    private readonly LibraryStore _lib;

    public MiniPlayerWindow(LibraryStore lib)
    {
        _lib = lib;
        InitializeComponent();
        SourceInitialized += (_, _) => RestorePlacement();
        LocationChanged += (_, _) => _dirty = true;
    }

    private bool _dirty;

    /// <summary>Raised when the user asks for the main window (⤢).</summary>
    public event Action? ExpandRequested;

    /// <summary>Raised when the user hides the bar (✕), so the app can remember it.</summary>
    public event Action? HideRequested;

    private void RestorePlacement()
    {
        var areas = WindowPlacement.CurrentWorkAreas(this);
        var saved = WindowPlacement.Parse(_lib.GetSetting(PlacementKey));
        var r = saved is { } s
            ? WindowPlacement.Clamp(new Rect(s.X, s.Y, Width, Height), areas)
            : DefaultSpot(areas);
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
        if (e.OriginalSource is FrameworkElement { TemplatedParent: System.Windows.Controls.Button }) return;
        try { DragMove(); } catch (InvalidOperationException) { return; }
        SavePlacement();
    }

    public void SavePlacement()
    {
        if (!_dirty) return;
        _dirty = false;
        _lib.SetSetting(PlacementKey, WindowPlacement.Format(new Rect(Left, Top, Width, Height)));
    }

    private void Expand_Click(object sender, RoutedEventArgs e) => ExpandRequested?.Invoke();

    private void Hide_Click(object sender, RoutedEventArgs e) => HideRequested?.Invoke();
}
