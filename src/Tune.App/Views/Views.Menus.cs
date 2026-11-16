using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Tune.App.ViewModels;
using Tune.Core.Feed;
using Tune.Core.Models;

namespace Tune.App.Views;

/// <summary>
/// Right-click / ⋯ menus: a card with a header (art + title) and an icon on every row — the same actions as the
/// phone's sheets. Playlists and albums get one menu everywhere: on their page, on cards and in the sidebar.
/// </summary>
public partial class Views
{
    /// <summary>A menu row with an icon (a geometry key from Theme.xaml, e.g. IcoPlay).</summary>
    internal static MenuItem Item(string header, Action action, string? icon = null, bool accent = false)
    {
        var item = new MenuItem { Header = header };
        if (icon is not null && Application.Current.TryFindResource(icon) is Geometry g)
        {
            var glyph = new Platform.Icon { Data = g, Size = 18 };
            glyph.SetResourceReference(Platform.Icon.ForegroundProperty, accent ? "AccentBrush" : "SubtleTextBrush");
            item.Icon = glyph;
        }
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>The menu's first row: the cover, title and a line under it (not clickable).</summary>
    internal static MenuItem HeaderItem(string? thumbnail, string title, string? subtitle)
    {
        var art = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(8), ClipToBounds = true, Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28)) };
        var image = new Image { Stretch = Stretch.UniformToFill };
        Platform.Art.SetUrl(image, thumbnail);
        art.Child = image;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), MaxWidth = 210 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrWhiteSpace(subtitle))
        {
            var sub = new TextBlock { Text = subtitle, FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "SubtleTextBrush");
            text.Children.Add(sub);
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(-4, 2, 0, 4) };
        row.Children.Add(art);
        row.Children.Add(text);
        return new MenuItem { Header = row, IsEnabled = false, Foreground = (Brush)Application.Current.FindResource("TextBrush") };
    }

    private static MenuItem PinItem(Card card)
    {
        var pinned = Shell.IsPinned(card);
        return Item(pinned ? "Unpin from speed dial" : "Pin to speed dial", () => Shell.TogglePin(card), "IcoPin", accent: pinned);
    }

    // ---------- playlists & albums ----------

    /// <summary>What a playlist/album menu is about: one of yours (by row id) or a YouTube Music album/playlist card.</summary>
    internal sealed record CollectionTarget(long? LocalId, Card? Card, string Title, string? Subtitle, string? Thumbnail);

    internal static CollectionTarget? TargetFor(Card card) => card.Kind switch
    {
        CardKind.Playlist when card.PlaylistId is { } p && p.StartsWith("tune:", StringComparison.Ordinal) =>
            Shell.Services.Library.PlaylistIdByUid(p[5..]) is { } id ? Local(id) : null,
        CardKind.Album or CardKind.Playlist => new CollectionTarget(null, card, card.Title, card.Subtitle, card.ThumbnailUrl),
        _ => null,
    };

    internal static CollectionTarget? Local(long id) =>
        Shell.Services.Library.GetPlaylists().FirstOrDefault(p => p.Id == id) is { } info
            ? new CollectionTarget(id, null, info.Name, $"Playlist · {ShellViewModel.Songs(info.Count)}", info.ThumbnailUrl)
            : null;

    internal static void ShowCollectionMenu(UIElement anchor, CollectionTarget target, bool atMouse)
    {
        var lib = Shell.Services.Library;
        var onItsPage = Shell.Page is CollectionViewModel page &&
                        (target.LocalId is { } lid ? page.PlaylistId == lid : page.SourceCard is { } sc && SpeedDialPins.PinKey(sc) == SpeedDialPins.PinKey(target.Card!));
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = atMouse ? PlacementMode.MousePoint : PlacementMode.Bottom };
        menu.Items.Add(HeaderItem(target.Thumbnail, target.Title, target.Subtitle));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Play", () => _ = PlayAsync(target, shuffle: false), "IcoPlay"));
        menu.Items.Add(Item("Shuffle", () => _ = PlayAsync(target, shuffle: true), "IcoShuffle"));
        if (!onItsPage)
            menu.Items.Add(Item("Open", () =>
            {
                if (target.LocalId is { } id) Shell.GoToCommand.Execute($"playlist:{id}");
                else Shell.OpenCardCommand.Execute(target.Card);
            }, "IcoOpen"));

        if (onItsPage && Shell.Page is CollectionViewModel c)
        {
            if (!c.IsDownloads && !c.AllDownloaded && c.Tracks.Count > 0)
                menu.Items.Add(Item(c.AnyDownloaded ? "Download the new songs" : "Download", () => c.ToggleDownloadAllCommand.Execute(null), "IcoDownload"));
            if (c.AnyDownloaded) menu.Items.Add(Item("Remove downloads", c.RemoveAllDownloads, "IcoDownloadDone", accent: true));
        }
        else if (target.LocalId is { } local)
        {
            var tracks = lib.GetPlaylistTracks(local);
            var states = tracks.Select(t => Shell.Services.Downloads.StateOf(t.VideoId)).ToList();
            if (states.Any(s => s == Tune.Core.Offline.DownloadState.None))
                menu.Items.Add(Item(states.Any(s => s != Tune.Core.Offline.DownloadState.None) ? "Download the new songs" : "Download", () => Shell.Download(tracks), "IcoDownload"));
            if (states.Any(s => s != Tune.Core.Offline.DownloadState.None))
                menu.Items.Add(Item("Remove downloads", () => Shell.RemoveDownloads(tracks), "IcoDownloadDone", accent: true));
        }

        var pin = target.LocalId is { } pid && lib.PlaylistUid(pid) is { } uid
            ? SpeedDialPins.LocalPlaylistCard(uid, target.Title, target.Thumbnail)
            : target.Card;
        if (pin is not null) menu.Items.Add(PinItem(pin));

        if (target.LocalId is { } own)
        {
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Rename", () =>
            {
                if (onItsPage && Shell.Page is CollectionViewModel cv) cv.BeginRenameCommand.Execute(null);
                else Shell.RenamePlaylist(own, target.Title);
            }, "IcoEdit"));
            menu.Items.Add(Item("Delete playlist", () =>
            {
                if (onItsPage && Shell.Page is CollectionViewModel cv) cv.DeletePlaylistCommand.Execute(null);
                else Shell.DeletePlaylist(own, target.Title);
            }, "IcoDelete"));
        }
        menu.IsOpen = true;
    }

    /// <summary>Selection bar: add the chosen songs to a playlist (or a new one).</summary>
    private void AddSelected_Click(object sender, RoutedEventArgs e)
    {
        if (Shell.Page is not CollectionViewModel c) return;
        var menu = new ContextMenu { PlacementTarget = (UIElement)sender, Placement = PlacementMode.Bottom };
        menu.Items.Add(Item("New playlist", () => c.AddSelectedTo(null), "IcoPlaylistAdd"));
        foreach (var p in Shell.Services.Library.GetPlaylists().Where(p => p.Id != c.PlaylistId))
            menu.Items.Add(Item(p.Name, () => c.AddSelectedTo(p)));
        menu.IsOpen = true;
    }

    private static async Task PlayAsync(CollectionTarget target, bool shuffle)
    {
        IReadOnlyList<Tune.Core.Models.Track> tracks;
        try
        {
            tracks = target.LocalId is { } id ? Shell.Services.Library.GetPlaylistTracks(id)
                : target.Card is { Kind: CardKind.Album, BrowseId: { } b } ? (await Shell.Services.Api.GetAlbumAsync(b)).Tracks
                : target.Card?.PlaylistId is { } p ? (await Shell.Services.Api.GetPlaylistAsync(p)).Tracks
                : [];
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Shell.ShowToast("Couldn't load this — check your connection");
            return;
        }
        if (tracks.Count == 0) return;
        if (shuffle) tracks = tracks.OrderBy(_ => Random.Shared.Next()).ToList();
        await Shell.Services.Playback.PlayListAsync(tracks, 0);
    }
}
