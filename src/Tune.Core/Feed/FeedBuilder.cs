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
    private const int QuickPicksMax = 20;
    private const int QuickPicksMin = 8;
    private const int QuickExtraSeeds = 3;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<Track> Tracks)> _radioCache = new();

    /// <summary>Randomness for refreshes (tests pin it).</summary>
    public Random Random { get; init; } = Random.Shared;

    /// <param name="fresh">A manual refresh: radios refetched, the "Because you played" seeds picked among more
    /// favourites and Quick picks reshuffled, so Home actually changes.</param>
    public async Task<FeedResult> BuildAsync(bool fresh = false, CancellationToken ct = default)
    {
        var now = clock.Now;
        var discoveryTask = DiscoveryAsync(ct);

        var shelves = new List<Shelf>();
        var recent = lib.GetRecent(RecentMax);
        if (recent.Count >= MinRowItems)
            shelves.Add(new Shelf("Recently played", recent.Select(SongCard).ToList(), IsLocal: true));

        var seeds = fresh
            ? WeightedPick(lib.GetTopTracks(now - SeedWindow, 8).Select(s => s.Track).ToList(), BecauseRows)
            : lib.GetTopTracks(now - SeedWindow, BecauseRows).Select(s => s.Track).ToList();
        if (seeds.Count == 0) seeds = recent.Take(BecauseRows).ToList();
        var radios = await Task.WhenAll(seeds.Select(s => RadioAsync(s.VideoId, now, ct, fresh))).ConfigureAwait(false);

        // more favourites feed Quick picks (their radios are fetched alongside)
        var favourites = lib.GetTopTracks(now - ArtistWindow, BecauseRows + QuickExtraSeeds + 6).Select(s => s.Track).ToList();
        var extraSeeds = favourites.Where(f => seeds.All(s => s.VideoId != f.VideoId)).Take(QuickExtraSeeds).ToList();
        var extraRadios = await Task.WhenAll(extraSeeds.Select(s => RadioAsync(s.VideoId, now, ct, fresh))).ConfigureAwait(false);

        var shown = new HashSet<string>(seeds.Select(s => s.VideoId));
        for (var i = 0; i < seeds.Count; i++)
        {
            if (radios[i] is not { } radio) continue;
            var items = radio.Where(t => shown.Add(t.VideoId)).Take(BecauseItems).Select(SongCard).ToList();
            if (items.Count > 0)
                shelves.Add(new Shelf("Because you played " + seeds[i].Title, items, IsLocal: true));
        }

        var picks = QuickPicks(radios, extraRadios, favourites, shown, fresh ? Random : null);
        if (picks.Count >= QuickPicksMin)
            shelves.Insert(recent.Count >= MinRowItems ? 1 : 0, new Shelf("Quick picks", picks.Select(SongCard).ToList(), IsLocal: true, IsList: true));

        var top = lib.GetTopArtists(now - ArtistWindow, TopArtists).Where(a => a.Artist.BrowseId is not null).ToList();
        var pictures = await Task.WhenAll(top.Select(a => ArtistPictureAsync(a.Artist.BrowseId!, now, ct))).ConfigureAwait(false);
        var artists = top
            .Select((a, i) => new Card(CardKind.Artist, a.Artist.Name, "Artist", pictures[i], null, a.Artist.BrowseId, null, null))
            .ToList();
        if (artists.Count >= MinRowItems)
            shelves.Add(new Shelf("Your top artists", artists, IsLocal: true));

        var (discovery, offline) = await discoveryTask.ConfigureAwait(false);
        shelves.AddRange(discovery);
        return new FeedResult(shelves, offline);
    }

    /// <summary>
    /// Like YouTube Music's Quick picks: mostly new songs close to what you play (round-robin across several radios,
    /// deeper than the "Because you played" rows so nothing repeats), with a familiar favourite every few songs.
    /// </summary>
    private static List<Track> QuickPicks(IReadOnlyList<Track>?[] becauseRadios, IReadOnlyList<Track>?[] extraRadios,
        IReadOnlyList<Track> favourites, HashSet<string> shown, Random? shuffle = null)
    {
        var pools = becauseRadios.Select(r => (r ?? []).Skip(BecauseItems + 1).ToList())
            .Concat(extraRadios.Select(r => (r ?? []).Skip(1).ToList()))
            .Where(p => p.Count > 0).ToList();
        if (shuffle is not null)
        {
            foreach (var p in pools) Shuffle(p, shuffle);
            favourites = Shuffled(favourites, shuffle);
        }
        var fresh = new List<Track>();
        for (var depth = 0; fresh.Count < QuickPicksMax && pools.Any(p => depth < p.Count); depth++)
            foreach (var pool in pools)
                if (depth < pool.Count && fresh.Count < QuickPicksMax && shown.Add(pool[depth].VideoId)) fresh.Add(pool[depth]);

        var familiar = new Queue<Track>(favourites.Where(f => shown.Add(f.VideoId)));
        var picks = new List<Track>();
        foreach (var t in fresh)
        {
            if (picks.Count >= QuickPicksMax) break;
            if (picks.Count % 5 == 2 && familiar.Count > 0) picks.Add(familiar.Dequeue()); // a favourite every 5 songs
            if (picks.Count < QuickPicksMax) picks.Add(t);
        }
        return picks;
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

    /// <summary>Picks <paramref name="count"/> items, favouring the front of the list (rank 1 is likelier than rank 8).</summary>
    private List<Track> WeightedPick(List<Track> ranked, int count)
    {
        var pool = ranked.ToList();
        var chosen = new List<Track>();
        while (chosen.Count < count && pool.Count > 0)
        {
            var total = pool.Count * (pool.Count + 1) / 2.0;
            var roll = Random.NextDouble() * total;
            var i = 0;
            for (var acc = (double)pool.Count; roll > acc && i < pool.Count - 1; acc += pool.Count - ++i) { }
            chosen.Add(pool[i]);
            pool.RemoveAt(i);
        }
        return chosen;
    }

    private static void Shuffle<T>(List<T> list, Random r)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = r.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static List<T> Shuffled<T>(IEnumerable<T> items, Random r)
    {
        var l = items.ToList();
        Shuffle(l, r);
        return l;
    }

    private async Task<IReadOnlyList<Track>?> RadioAsync(string videoId, DateTimeOffset now, CancellationToken ct, bool bypassCache = false)
    {
        if (!bypassCache && _radioCache.TryGetValue(videoId, out var hit) && now - hit.At < RadioTtl) return hit.Tracks;
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

    private static readonly TimeSpan PictureTtl = TimeSpan.FromHours(24);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Url)> _artistPictures = new();

    /// <summary>History only knows artist names; the picture comes from the artist page (cached a day).</summary>
    private async Task<string?> ArtistPictureAsync(string browseId, DateTimeOffset now, CancellationToken ct)
    {
        if (_artistPictures.TryGetValue(browseId, out var hit) && now - hit.At < PictureTtl) return hit.Url;
        try
        {
            var page = await api.GetArtistAsync(browseId, ct).ConfigureAwait(false);
            _artistPictures[browseId] = (now, page.ThumbnailUrl);
            return page.ThumbnailUrl;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Artist picture for {browseId} unavailable", e);
            return null;
        }
    }

    private static Card SongCard(Track t) =>
        new(CardKind.Song, t.Title, t.ArtistLine, t.ThumbnailUrl, t.VideoId, null, null, t);
}
