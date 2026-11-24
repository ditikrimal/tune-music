using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Tune.App.ViewModels;

namespace Tune.App.Views;

public partial class QueuePanel : UserControl
{
    public QueuePanel() => InitializeComponent();

    private QueueViewModel? Vm => DataContext as QueueViewModel;
    private static ShellViewModel Shell => App.Shell;

    private static TrackRowViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as TrackRowViewModel;

    private Point _pressAt;
    private bool _dragged;

    private void Row_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressAt = e.GetPosition(this);
        _dragged = false;
    }

    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if (_dragged) return; // the press became a drag
        if (Row(sender) is { IsCurrent: false } row)
        {
            if (row.IsUnavailable)
            {
                Shell.ShowToast($"{row.Title} isn't downloaded — it plays when you're back online");
                return;
            }
            Vm?.JumpCommand.Execute(row);
        }
    }

    /// <summary>The wheel scrolls the chip row sideways.</summary>
    private void Chips_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        Vm?.RemoveCommand.Execute(Row(sender));
        e.Handled = true;
    }

    // ---------- drag to reorder ----------

    private void Row_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || Row(sender) is not { IsCurrent: false } row) return;
        var d = e.GetPosition(this) - _pressAt;
        if (Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance * 2 && Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance * 2) return;
        _dragged = true;
        var el = (FrameworkElement)sender;
        el.Opacity = 0.5;
        try { DragDrop.DoDragDrop(el, row, DragDropEffects.Move); }
        finally { el.Opacity = 1; }
    }

    private void Row_DragEnter(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement el && Row(sender) is { IsCurrent: false }) el.Tag = "drop";
    }

    private void Row_DragLeave(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement el) el.Tag = null;
    }

    private void Row_Drop(object sender, DragEventArgs e)
    {
        if (sender is FrameworkElement el) el.Tag = null;
        if (e.Data.GetData(typeof(TrackRowViewModel)) is TrackRowViewModel dragged && Row(sender) is { IsCurrent: false } target && dragged != target)
            Vm?.Move(dragged, target);
    }

    // ---------- right-click menu ----------

    private void Row_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Row(sender) is not { } row || Vm is not { } vm) return;
        e.Handled = true;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        if (!row.IsCurrent)
        {
            menu.Items.Add(Item("Play now", () => vm.JumpCommand.Execute(row)));
            menu.Items.Add(Item("Play next", () => vm.PlayNext(row)));
            menu.Items.Add(Item("Remove from queue", () => vm.RemoveCommand.Execute(row)));
        }
        menu.Items.Add(Item(row.IsLiked ? "Remove from Liked songs" : "Save to Liked songs", () =>
        {
            row.IsLiked = !row.IsLiked;
            Shell.SetLiked(row.Track, row.IsLiked);
        }));
        var dl = Shell.Services.Downloads.StateOf(row.Track.VideoId);
        menu.Items.Add(dl == Tune.Core.Offline.DownloadState.None
            ? Item("Download", () => Shell.Download([row.Track]))
            : Item(dl == Tune.Core.Offline.DownloadState.Done ? "Remove download" : "Cancel download", () => Shell.RemoveDownloads([row.Track])));
        foreach (var artist in row.Track.Artists.Where(a => a.BrowseId is not null).Take(2))
            menu.Items.Add(Item($"Go to {artist.Name}", () => Shell.OpenArtistCommand.Execute(artist)));
        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }
}
