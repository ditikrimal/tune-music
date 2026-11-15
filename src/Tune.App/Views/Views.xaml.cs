using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Tune.App.ViewModels;

namespace Tune.App.Views;

/// <summary>Event handlers for the page templates in Views.xaml.</summary>
public partial class Views : ResourceDictionary
{
    public Views() => InitializeComponent();

    private static ShellViewModel Shell => App.Shell;

    private static T? Ancestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = VisualTreeHelper.GetParent(d);
        return d as T;
    }

    // ---------- shelves ----------

    private static ScrollViewer? ShelfScroller(object sender) =>
        Ancestor<DockPanel>((DependencyObject)sender)?.Parent is StackPanel panel ? panel.Children.OfType<ScrollViewer>().FirstOrDefault() : null;

    private void ShelfLeft_Click(object sender, RoutedEventArgs e)
    {
        if (ShelfScroller(sender) is { } sv) Platform.SmoothScroll.ScrollBy(sv, -sv.ViewportWidth * 0.8, horizontal: true);
    }

    private void ShelfRight_Click(object sender, RoutedEventArgs e)
    {
        if (ShelfScroller(sender) is { } sv) Platform.SmoothScroll.ScrollBy(sv, sv.ViewportWidth * 0.8, horizontal: true);
    }

    // ---------- track rows ----------

    private static TrackRowViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as TrackRowViewModel;

    private void TrackRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        switch (Shell.Page)
        {
            case CollectionViewModel c: c.PlayRowCommand.Execute(row); break;
            case ArtistViewModel a: a.PlayRowCommand.Execute(row); break;
            case SearchViewModel s: s.PlaySongCommand.Execute(row); break;
        }
    }

    // pointing at a song is a strong hint it's next: look it up now so the click starts instantly
    private void TrackRow_MouseEnter(object sender, MouseEventArgs e)
    {
        if (Row(sender) is { } row) Shell.Prefetch([row.Track], Tune.Core.Streams.PrefetchPriority.Hover);
    }

    private void Card_MouseEnter(object sender, MouseEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Tune.Core.Models.Card { Track: { } t })
            Shell.Prefetch([t], Tune.Core.Streams.PrefetchPriority.Hover);
    }

    // ---------- card quick actions ----------

    private static Tune.Core.Models.Track? CardTrack(object sender) =>
        ((sender as FrameworkElement)?.DataContext as Tune.Core.Models.Card)?.Track;

    private void CardLike_Click(object sender, RoutedEventArgs e)
    {
        if (CardTrack(sender) is not { } t) return;
        var liked = !Shell.Services.Library.IsLiked(t.VideoId);
        Shell.SetLiked(t, liked);
        Shell.ShowToast(liked ? "Saved to Liked songs" : "Removed from Liked songs");
        if (sender is Button b) b.Content = liked ? "\uEB52" : "\uEB51";
        e.Handled = true;
    }

    private void CardMore_Click(object sender, RoutedEventArgs e)
    {
        if (CardTrack(sender) is not { } t) return;
        ShowTrackMenu((FrameworkElement)sender, new TrackRowViewModel(t, 0) { IsLiked = Shell.Services.Library.IsLiked(t.VideoId) });
        e.Handled = true;
    }

    private void Like_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is not { } row) return;
        row.IsLiked = !row.IsLiked;
        Shell.SetLiked(row.Track, row.IsLiked);
    }

    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (Row(sender) is { } row) ShowTrackMenu((FrameworkElement)sender, row);
    }

    private void TrackRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Row(sender) is { } row) ShowTrackMenu((FrameworkElement)sender, row);
        e.Handled = true;
    }

    private static void ShowTrackMenu(FrameworkElement anchor, TrackRowViewModel row)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Play next", () => Shell.PlayNext(row.Track)));

        var add = new MenuItem { Header = "Add to playlist" };
        add.Items.Add(Item("＋ New playlist", () => Shell.AddToNewPlaylist(row.Track)));
        foreach (var p in Shell.Services.Library.GetPlaylists())
            add.Items.Add(Item(p.Name, () => Shell.AddToPlaylist(row.Track, p)));
        menu.Items.Add(add);

        menu.Items.Add(Item(row.IsLiked ? "Remove from Liked songs" : "Save to Liked songs", () =>
        {
            row.IsLiked = !row.IsLiked;
            Shell.SetLiked(row.Track, row.IsLiked);
        }));

        if (Shell.Page is CollectionViewModel { IsLocal: true } c)
            menu.Items.Add(Item("Remove from this playlist", () => c.Remove(row)));

        foreach (var artist in row.Track.Artists.Where(a => a.BrowseId is not null).Take(3))
            menu.Items.Add(Item($"Go to {artist.Name}", () => Shell.OpenArtistCommand.Execute(artist)));

        if (row.Track.Album is { BrowseId: not null } album)
            menu.Items.Add(Item("Go to album", () => Shell.OpenCardCommand.Execute(
                new Tune.Core.Models.Card(Tune.Core.Models.CardKind.Album, album.Title, row.ArtistLine, row.ThumbnailUrl, null, album.BrowseId, null, null))));

        menu.IsOpen = true;
    }

    private static MenuItem Item(string header, Action action)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => action();
        return item;
    }

    // ---------- search ----------

    private void SearchBox_Loaded(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }

    // ---------- local playlist header ----------

    private void Title_Click(object sender, MouseButtonEventArgs e)
    {
        if (Shell.Page is CollectionViewModel { IsLocal: true } c) c.BeginRenameCommand.Execute(null);
    }

    private void PlaylistMenu_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.Page is not CollectionViewModel { IsLocal: true } c) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("Rename", () => c.BeginRenameCommand.Execute(null)));
        menu.Items.Add(Item("Delete playlist", () => c.DeletePlaylistCommand.Execute(null)));
        menu.IsOpen = true;
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        var box = (TextBox)sender;
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            box.SelectAll();
        });
    }

    private void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (Shell.Page is not CollectionViewModel c) return;
        if (e.Key == Key.Enter) c.CommitRename(((TextBox)sender).Text);
        else if (e.Key == Key.Escape) c.IsRenaming = false;
    }

    private void RenameBox_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (Shell.Page is CollectionViewModel { IsRenaming: true } c) c.CommitRename(((TextBox)sender).Text);
    }

    // ---------- drag to reorder (local playlists) ----------

    private Point _dragStart;
    private TrackRowViewModel? _dragRow;

    private void Tracks_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        _dragRow = (e.OriginalSource as FrameworkElement)?.DataContext as TrackRowViewModel;
    }

    private void Tracks_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragRow is null) return;
        if (Shell.Page is not CollectionViewModel { IsLocal: true }) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance * 2) return;
        var row = _dragRow;
        _dragRow = null;
        DragDrop.DoDragDrop((DependencyObject)sender, row, DragDropEffects.Move);
    }

    private void Tracks_Drop(object sender, DragEventArgs e)
    {
        if (Shell.Page is not CollectionViewModel c || e.Data.GetData(typeof(TrackRowViewModel)) is not TrackRowViewModel dragged) return;
        var target = (e.OriginalSource as FrameworkElement)?.DataContext as TrackRowViewModel;
        var from = c.Tracks.IndexOf(dragged);
        var to = target is null ? c.Tracks.Count - 1 : c.Tracks.IndexOf(target);
        if (from >= 0 && to >= 0) c.Move(from, to);
    }
}
