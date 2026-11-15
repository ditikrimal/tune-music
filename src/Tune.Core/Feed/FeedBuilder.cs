using System.Collections.Concurrent;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.Feed;

public sealed record FeedResult(IReadOnlyList<Shelf> Shelves, bool Offline);

/// <summary>Builds the Home feed: local rows from play history first, then YouTube Music's anonymous discovery rows.</summary>
public sealed class FeedBuilder(LibraryStore lib, IYtMusic api, IClock clock)
{
    private const int RecentMax = 12;
    private const int MinRowItems = 3;
    private const int BecauseRows = 3;
    private const int BecauseItems = 12;
    private const int TopArtists = 8;
    private static readonly TimeSpan SeedWindow = TimeSpan.FromDays(14);
    private static readonly TimeSpan ArtistWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan RadioTtl = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<Track> Tracks)> _radioCache = new();

    public async Task<FeedResult> BuildAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var discoveryTask = DiscoveryAsync(ct);

        var shelves = new List<Shelf>();
        var recent = lib.GetRecent(RecentMax);
        if (recent.Count >= MinRowItems)
            shelves.Add(new Shelf("Recently played", recent.Select(SongCard).ToList(), IsLocal: true));

        var seeds = lib.GetTopTracks(now - SeedWindow, BecauseRows).Select(s => s.Track).ToList();
        if (seeds.Count == 0) seeds = recent.Take(BecauseRows).ToList();
        var radios = await Task.WhenAll(seeds.Select(s => RadioAsync(s.VideoId, now, ct))).ConfigureAwait(false);

        var shown = new HashSet<string>(seeds.Select(s => s.VideoId));
        for (var i = 0; i < seeds.Count; i++)
        {
            if (radios[i] is not { } radio) continue;
            var items = radio.Where(t => shown.Add(t.VideoId)).Take(BecauseItems).Select(SongCard).ToList();
            if (items.Count > 0)
                shelves.Add(new Shelf("Because you played " + seeds[i].Title, items, IsLocal: true));
        }

        var artists = lib.GetTopArtists(now - ArtistWindow, TopArtists)
            .Where(a => a.Artist.BrowseId is not null)
            .Select(a => new Card(CardKind.Artist, a.Artist.Name, "Artist", null, null, a.Artist.BrowseId, null, null))
            .ToList();
        if (artists.Count >= MinRowItems)
            shelves.Add(new Shelf("Your top artists", artists, IsLocal: true));

        var (discovery, offline) = await discoveryTask.ConfigureAwait(false);
        shelves.AddRange(discovery);
        return new FeedResult(shelves, offline);
    }

    private async Task<(IReadOnlyList<Shelf>, bool Offline)> DiscoveryAsync(CancellationToken ct)
    {
        try
        {
            return (await api.GetHomeAsync(ct).ConfigureAwait(false), false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn("Discovery rows unavailable", e);
            return ([], true);
        }
    }

    private async Task<IReadOnlyList<Track>?> RadioAsync(string videoId, DateTimeOffset now, CancellationToken ct)
    {
        if (_radioCache.TryGetValue(videoId, out var hit) && now - hit.At < RadioTtl) return hit.Tracks;
        try
        {
            var tracks = await api.GetRadioAsync(videoId, ct).ConfigureAwait(false);
            _radioCache[videoId] = (now, tracks);
            return tracks;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Radio for {videoId} unavailable", e);
            return null;
        }
    }

    private static Card SongCard(Track t) =>
        new(CardKind.Song, t.Title, t.ArtistLine, t.ThumbnailUrl, t.VideoId, null, null, t);
}
