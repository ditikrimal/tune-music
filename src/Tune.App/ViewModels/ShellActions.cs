using CommunityToolkit.Mvvm.Input;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Util;

namespace Tune.App.ViewModels;

/// <summary>Actions any page can trigger: open/play cards, artists, playlists, queue edits.</summary>
public sealed partial class ShellViewModel
{
    private HomeViewModel? _home;
    private SearchViewModel? _search;

    public void InitPages() => PageFactory = key => key switch
    {
        "home" => _home ??= new HomeViewModel(this),
        "search" => _search ??= new SearchViewModel(this),
        "liked" => CollectionViewModel.Liked(this),
        "history" => CollectionViewModel.History(this),
        "downloads" => CollectionViewModel.Downloads(this),
        "settings" => new SettingsViewModel(this),
        _ when key.StartsWith("playlist:", StringComparison.Ordinal) && long.TryParse(key[9..], out var id) =>
            CollectionViewModel.LocalPlaylist(this, id),
        _ => new PlaceholderPage(key, key),
    };

    /// <summary>Click on a card: songs start playing (with radio), everything else opens its page.</summary>
    [RelayCommand]
    private async Task OpenCard(Card? card)
    {
        if (card is null) return;
        switch (card.Kind)
        {
            case CardKind.Song when card.Track is not null:
                await Services.Playback.PlayRadioAsync(card.Track);
                break;
            case CardKind.Album when card.BrowseId is not null:
                Navigate(CollectionViewModel.Album(this, card));
                break;
            case CardKind.Playlist when card.PlaylistId is not null:
                Navigate(CollectionViewModel.YtPlaylist(this, card));
                break;
            case CardKind.Artist when card.BrowseId is not null:
                Navigate(new ArtistViewModel(this, card.BrowseId, card.Title));
                break;
        }
    }

    /// <summary>The green play button on a card: plays an album/playlist straight away.</summary>
    [RelayCommand]
    private async Task PlayCard(Card? card)
    {
        if (card is null) return;
        try
        {
            var tracks = card.Kind switch
            {
                CardKind.Song when card.Track is not null => null,
                CardKind.Album when card.BrowseId is not null => (await Services.Api.GetAlbumAsync(card.BrowseId)).Tracks,
                CardKind.Playlist when card.PlaylistId is not null => (await Services.Api.GetPlaylistAsync(card.PlaylistId)).Tracks,
                CardKind.Artist when card.BrowseId is not null => (await Services.Api.GetArtistAsync(card.BrowseId)).TopSongs,
                _ => null,
            };
            if (tracks is { Count: > 0 }) await Services.Playback.PlayListAsync(tracks, 0);
            else if (card.Track is not null) await Services.Playback.PlayRadioAsync(card.Track);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Log.Warn("Play card failed", e);
            ShowToast("Couldn't load that — check your connection.");
        }
    }

    [RelayCommand]
    private void OpenPlaylist(PlaylistInfo? playlist)
    {
        if (playlist is not null) GoToCommand.Execute($"playlist:{playlist.Id}");
    }

    public static string Songs(int n) => n == 1 ? "1 song" : $"{n} songs";

    [RelayCommand]
    private void OpenArtist(ArtistRef? artist)
    {
        if (artist?.BrowseId is { } id) Navigate(new ArtistViewModel(this, id, artist.Name));
    }

    /// <summary>Prepare songs the user can see (or is pointing at) so clicking them starts instantly.</summary>
    public void Prefetch(IEnumerable<Track> tracks, Tune.Core.Streams.PrefetchPriority priority) =>
        Services.Prefetcher.Request(tracks.Select(t => t.VideoId).ToList(), priority);

    public void PlayNext(Track t)
    {
        Services.Playback.Queue.PlayNext(t);
        Services.Playback.NotifyQueueChanged();
        ShowToast("Will play next");
    }

    public void AddToPlaylist(Track t, PlaylistInfo playlist)
    {
        Services.Library.AddToPlaylist(playlist.Id, t);
        ReloadPlaylists();
        ShowToast($"Added to {playlist.Name}");
        if (Page is CollectionViewModel { PlaylistId: { } id } page && id == playlist.Id) page.Reload();
    }

    /// <summary>Imports a YouTube Music playlist from a link into the library. Returns a message and the new playlist id.</summary>
    public async Task<(string Message, long? Id)> ImportPlaylistAsync(string text)
    {
        if (Tune.Core.YtMusic.PlaylistLinks.Parse(text) is not { } listId)
            return ("That doesn\u2019t look like a playlist link. It should contain list=\u2026", null);
        try
        {
            var c = await Services.Api.GetPlaylistAsync(listId);
            if (c.Tracks.Count == 0) return ("That playlist is empty or private (only public and unlisted playlists can be imported).", null);
            var id = Services.Library.CreatePlaylist(c.Title);
            Services.Library.AddManyToPlaylist(id, c.Tracks);
            ReloadPlaylists();
            ShowToast($"Imported {c.Title}");
            return ($"Imported {Songs(c.Tracks.Count)} into \u201c{c.Title}\u201d.", id);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Tune.Core.Util.Log.Warn("Playlist import failed", e);
            return ("Couldn\u2019t import \u2014 check the link and your connection.", null);
        }
    }

    /// <summary>Asks for a playlist name (null = cancelled). Set by the main window.</summary>
    public Func<string, string?> AskPlaylistName { get; set; } = s => s;

    public string SuggestedPlaylistName => $"My playlist #{Services.Library.GetPlaylists().Count + 1}";

    public void AddToNewPlaylist(Track t)
    {
        if (AskPlaylistName(SuggestedPlaylistName) is not { } name) return;
        var lib = Services.Library;
        var id = lib.CreatePlaylist(name);
        lib.AddToPlaylist(id, t);
        ReloadPlaylists();
        ShowToast($"Added to {name}");
    }

    public void SetLiked(Track t, bool liked)
    {
        Services.Library.SetLiked(t, liked);
        Player.Refresh();
        LikesChanged();
        if (Page is CollectionViewModel { IsLiked: true } page) page.Reload();
    }
}
