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
        if (row.IsUnavailable)
        {
            Shell.ShowToast($"{row.Title} isn't downloaded — it plays when you're back online");
            return;
        }
        switch (Shell.Page)
        {
            case CollectionViewModel c: c.PlayRowCommand.Execute(row); break;
            case ArtistViewModel a: a.PlayRowCommand.Execute(row); break;
            case SearchViewModel s: s.PlaySongCommand.Execute(row); break;
            case HomeViewModel h: h.PlayOfflineRowCommand.Execute(row); break;
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
        e.Handled = true;
    }

    private void Card_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Tune.Core.Models.Card card) return;
        e.Handled = true;
        if (card.Track is { } t)
        {
            ShowTrackMenu((FrameworkElement)sender, new TrackRowViewModel(t, 0) { IsLiked = Shell.Services.Library.IsLiked(t.VideoId) });
            return;
        }
        if (TargetFor(card) is { } target)
        {
            ShowCollectionMenu((UIElement)sender, target, atMouse: true);
            return;
        }
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.MousePoint };
        menu.Items.Add(HeaderItem(card.ThumbnailUrl, card.Title, card.Subtitle));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Go to artist", () => Shell.OpenCardCommand.Execute(card), "IcoPerson"));
        menu.Items.Add(Item("Play their top songs", () => Shell.PlayCardCommand.Execute(card), "IcoPlay"));
        menu.IsOpen = true;
    }

    // ---------- Quick picks: the row's songs become the queue ----------

    private static List<Tune.Core.Models.Track> ShelfTracks(object sender) =>
        Ancestor<ItemsControl>(sender as DependencyObject) is { } list && list.ItemsSource is IEnumerable<Tune.Core.Models.Card> cards
            ? cards.Where(c => c.Track is not null).Select(c => c.Track!).ToList()
            : (sender as FrameworkElement)?.DataContext is Tune.Core.Models.Shelf shelf
                ? shelf.Items.Where(c => c.Track is not null).Select(c => c.Track!).ToList()
                : [];

    /// <summary>Speed dial: a song plays with the dial's other songs after it; a pinned album/playlist opens.</summary>
    private void SpeedDialTile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Tune.Core.Models.Card card) return;
        if (card.Track is { } t)
        {
            var tracks = ShelfTracks(sender);
            _ = Shell.Services.Playback.PlayListAsync(tracks, Math.Max(0, tracks.FindIndex(x => x.VideoId == t.VideoId)));
        }
        else Shell.OpenCardCommand.Execute(card);
    }

    private void QuickPick_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Tune.Core.Models.Card { Track: { } t }) return;
        if (e.OriginalSource is DependencyObject d && Ancestor<Button>(d) is not null) return; // the ⋯ button
        var tracks = ShelfTracks(sender);
        _ = Shell.Services.Playback.PlayListAsync(tracks, Math.Max(0, tracks.FindIndex(x => x.VideoId == t.VideoId)));
    }

    private void QuickPicksPlayAll_Click(object sender, RoutedEventArgs e)
    {
        var tracks = ShelfTracks(sender);
        if (tracks.Count > 0) _ = Shell.Services.Playback.PlayListAsync(tracks, 0);
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
        menu.Items.Add(HeaderItem(row.Track.ThumbnailUrl, row.Track.Title, row.Track.ArtistLine));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Start radio", () => _ = Shell.Services.Playback.PlayRadioAsync(row.Track), "IcoRadio"));
        menu.Items.Add(Item("Play next", () => Shell.PlayNext(row.Track), "IcoPlayNext"));

        var add = Item("Add to playlist", () => { }, "IcoPlaylistAdd");
        add.Items.Add(Item("New playlist", () => Shell.AddToNewPlaylist(row.Track), "IcoPlaylistAdd"));
        foreach (var p in Shell.Services.Library.GetPlaylists())
            add.Items.Add(Item(p.Name, () => Shell.AddToPlaylist(row.Track, p)));
        menu.Items.Add(add);

        menu.Items.Add(Item(row.IsLiked ? "Remove from Liked songs" : "Save to Liked songs", () =>
        {
            row.IsLiked = !row.IsLiked;
            Shell.SetLiked(row.Track, row.IsLiked);
        }, row.IsLiked ? "IcoHeart" : "IcoHeartOutline", accent: row.IsLiked));

        if (Shell.Page is CollectionViewModel { IsLocal: true } c)
            menu.Items.Add(Item("Remove from this playlist", () => c.Remove(row), "IcoDelete"));

        var songCard = new Tune.Core.Models.Card(Tune.Core.Models.CardKind.Song, row.Track.Title, row.Track.ArtistLine, row.Track.ThumbnailUrl,
            row.Track.VideoId, null, null, row.Track);
        menu.Items.Add(PinItem(songCard));

        var dl = Shell.Services.Downloads.StateOf(row.Track.VideoId);
        menu.Items.Add(dl == Tune.Core.Offline.DownloadState.None
            ? Item("Download", () => Shell.Download([row.Track]), "IcoDownload")
            : Item(dl == Tune.Core.Offline.DownloadState.Done ? "Remove download" : "Cancel download", () => Shell.RemoveDownloads([row.Track]),
                dl == Tune.Core.Offline.DownloadState.Done ? "IcoDownloadDone" : "IcoClose", accent: dl == Tune.Core.Offline.DownloadState.Done));
        menu.Items.Add(new Separator());

        foreach (var artist in row.Track.Artists.Where(a => a.BrowseId is not null).Take(3))
            menu.Items.Add(Item($"Go to {artist.Name}", () => Shell.OpenArtistCommand.Execute(artist), "IcoPerson"));

        if (row.Track.Album is { BrowseId: not null } album)
            menu.Items.Add(Item("Go to album", () => Shell.OpenCardCommand.Execute(
                new Tune.Core.Models.Card(Tune.Core.Models.CardKind.Album, album.Title, row.ArtistLine, row.ThumbnailUrl, null, album.BrowseId, null, null)), "IcoAlbum"));

        menu.IsOpen = true;
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
        if (Shell.Page is not CollectionViewModel c) return;
        CollectionTarget? target = c.Kind switch
        {
            CollectionKind.LocalPlaylist when c.PlaylistId is { } id => Local(id),
            CollectionKind.Album or CollectionKind.YtPlaylist when c.SourceCard is { } card =>
                new CollectionTarget(null, card, c.Title, c.Subtitle, c.ThumbnailUrl ?? card.ThumbnailUrl),
            _ => null,
        };
        if (target is not null)
        {
            ShowCollectionMenu((UIElement)sender, target, atMouse: false);
            return;
        }
        // Downloads / Liked songs: just the download actions
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        if (!c.IsDownloads && !c.AllDownloaded && c.Tracks.Count > 0)
            menu.Items.Add(Item(c.AnyDownloaded ? "Download the new songs" : "Download", () => c.ToggleDownloadAllCommand.Execute(null), "IcoDownload"));
        if (c.AnyDownloaded)
            menu.Items.Add(Item(c.IsDownloads ? "Remove all downloads" : "Remove downloads", c.RemoveAllDownloads, "IcoDownloadDone", accent: true));
        if (menu.Items.Count > 0) menu.IsOpen = true;
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
