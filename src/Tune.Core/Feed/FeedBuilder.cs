using System.Collections.Concurrent;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.Feed;

public sealed record FeedResult(IReadOnlyList<Shelf> Shelves, bool Offline);

/// <summary>
/// Builds Home in the user's layout (Settings > Home): speed dial, quick picks, because-you-played, forgotten
/// favourites, top artists, recently played, then YouTube Music's own rows. Everything personal is ranked by
/// <see cref="Taste"/> (plays with recency, likes, skips) and changes day to day; a manual refresh reshuffles.
/// </summary>
public sealed class FeedBuilder(LibraryStore lib, IYtMusic api, IClock clock)
{
    private const int MinRowItems = 3;
    private const int BecauseRows = 3;
    private const int BecauseItems = 12;
    private const int RadioSeeds = 6;
    private const int SpeedDialMax = 27;
    private const int QuickPicksMax = 20;
    private const int QuickPicksMin = 8;
    private const int QuickFamiliar = 6;
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(365);
    private static readonly TimeSpan RadioTtl = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<Track> Tracks)> _radioCache = new();

    /// <summary>Randomness for refreshes (tests pin it).</summary>
    public Random Random { get; init; } = Random.Shared;

    public Taste LoadTaste()
    {
        var now = clock.Now;
        return new Taste(lib.GetPlays(now - HistoryWindow), lib.GetLiked(), lib.GetSkips(now - TimeSpan.FromDays(90)), now, lib.GetNotInterested());
    }

    /// <param name="fresh">A manual refresh: radios refetched, other seeds, quick picks reshuffled.</param>
    public async Task<FeedResult> BuildAsync(bool fresh = false, CancellationToken ct = default)
    {
        var now = clock.Now;
        var layout = HomeLayout.Load(lib).Where(s => s.Visible).Select(s => s.Section).ToList();
        var discoveryTask = layout.Contains(HomeSection.Discovery) ? DiscoveryAsync(ct) : Task.FromResult<(IReadOnlyList<Shelf>, bool)>(([], false));
        var taste = LoadTaste();
        // the same picks all day (Home doesn't jump around between opens); new ones tomorrow or on refresh
        var random = fresh ? Random : new Random(now.UtcDateTime.DayOfYear * 7919 + now.Year);

        var seeds = taste.Seeds(RadioSeeds, random).ToList();
        if (seeds.Count == 0) seeds = lib.GetRecent(RadioSeeds).ToList();
        var radios = await Task.WhenAll(seeds.Select(s => RadioAsync(s.VideoId, now, ct, fresh))).ConfigureAwait(false);

        var shelves = new Dictionary<HomeSection, List<Shelf>>();
        var shown = new HashSet<string>(); // songs already on Home (no row repeats another)

        if (layout.Contains(HomeSection.SpeedDial) && SpeedDial(taste) is { Count: > 0 } dial)
            shelves[HomeSection.SpeedDial] = [new Shelf("Speed dial", dial, IsLocal: true, IsSpeedDial: true)];

        if (layout.Contains(HomeSection.QuickPicks))
        {
            var picks = QuickPicks(taste, radios, shown, random, jitter: fresh);
            if (picks.Count >= QuickPicksMin)
            {
                shelves[HomeSection.QuickPicks] = [new Shelf("Quick picks", picks.Select(SongCard).ToList(), IsLocal: true, IsList: true)];
                foreach (var t in picks) shown.Add(t.VideoId);
            }
        }

        if (layout.Contains(HomeSection.Because))
        {
            var rows = new List<Shelf>();
            for (var i = 0; i < seeds.Count && rows.Count < BecauseRows; i++)
            {
                if (radios[i] is not { } radio) continue;
                var items = Taste.CapPerArtist(radio.Skip(1).Where(t => !taste.SkippedIds.Contains(t.VideoId) && !shown.Contains(t.VideoId)
                                                                     && seeds.All(s => s.VideoId != t.VideoId)), 3)
                    .Take(BecauseItems).ToList();
                foreach (var t in items) shown.Add(t.VideoId);
                if (items.Count > 0) rows.Add(new Shelf("Because you played " + seeds[i].Title, items.Select(SongCard).ToList(), IsLocal: true));
            }
            shelves[HomeSection.Because] = rows;
        }

        if (layout.Contains(HomeSection.Forgotten) && taste.Forgotten(BecauseItems) is { Count: >= MinRowItems } forgotten)
            shelves[HomeSection.Forgotten] = [new Shelf("Forgotten favourites", forgotten.Select(SongCard).ToList(), IsLocal: true)];

        if (layout.Contains(HomeSection.Artists))
        {
            var top = taste.TopArtists(8);
            var pictures = await Task.WhenAll(top.Select(a => ArtistPictureAsync(a.BrowseId!, now, ct))).ConfigureAwait(false);
            var artists = top.Select((a, i) => new Card(CardKind.Artist, a.Name, "Artist", pictures[i], null, a.BrowseId, null, null)).ToList();
            if (artists.Count >= MinRowItems) shelves[HomeSection.Artists] = [new Shelf("Your top artists", artists, IsLocal: true)];
        }

        if (layout.Contains(HomeSection.Recent) && lib.GetRecent(12) is { Count: >= MinRowItems } recent)
            shelves[HomeSection.Recent] = [new Shelf("Recently played", recent.Select(SongCard).ToList(), IsLocal: true)];

        var (discovery, offline) = await discoveryTask.ConfigureAwait(false);
        shelves[HomeSection.Discovery] = discovery.ToList();
        return new FeedResult(layout.SelectMany(s => shelves.GetValueOrDefault(s) ?? []).ToList(), offline);
    }

    /// <summary>Pins first (your own playlists kept up to date: name, art, deleted), then the songs you come back to.</summary>
    public IReadOnlyList<Card> SpeedDial(Taste taste)
    {
        var cards = new List<Card>();
        foreach (var pin in SpeedDialPins.Load(lib))
        {
            if (pin.PlaylistId is { } p && p.StartsWith("tune:", StringComparison.Ordinal))
            {
                if (lib.PlaylistIdByUid(p[5..]) is not { } id) continue; // deleted
                var info = lib.GetPlaylists().FirstOrDefault(x => x.Id == id);
                if (info is null) continue;
                cards.Add(pin with { Title = info.Name, ThumbnailUrl = info.ThumbnailUrl ?? pin.ThumbnailUrl, Pinned = true });
            }
            else cards.Add(pin with { Pinned = true });
        }
        var pinnedSongs = cards.Where(c => c.Kind == CardKind.Song).Select(c => c.VideoId).Concat(taste.SkippedIds).ToHashSet();
        var songs = taste.SpeedDial(SpeedDialMax).ToList();
        // little history yet: fill a full page with what you played lately and your likes
        if (songs.Count < 9)
            songs.AddRange(lib.GetRecent(20).Concat(lib.GetLiked()).Where(t => songs.All(s => s.VideoId != t.VideoId)).Take(9 - songs.Count));
        cards.AddRange(songs.Where(t => !pinnedSongs.Contains(t.VideoId)).DistinctBy(t => t.VideoId).Select(SongCard));
        return cards.Take(SpeedDialMax).ToList();
    }

    /// <summary>
    /// Like YouTube Music's Quick picks: mostly new songs, ranked by how many of your radios suggest them (and how
    /// early), boosted by artists you love; nothing you heard in the last day and a half or keep skipping; at most
    /// two per artist; with a familiar favourite you haven't heard today every third song.
    /// </summary>
    private static List<Track> QuickPicks(Taste taste, IReadOnlyList<Track>?[] radios, IReadOnlySet<string> shown, Random random, bool jitter)
    {
        var candidates = new Dictionary<string, (Track Track, double Score)>();
        foreach (var radio in radios)
        {
            if (radio is null) continue;
            for (var rank = 1; rank < radio.Count; rank++) // [0] is the seed itself
            {
                var t = radio[rank];
                if (shown.Contains(t.VideoId) || taste.SkippedIds.Contains(t.VideoId)) continue;
                var c = candidates.GetValueOrDefault(t.VideoId, (t, 0));
                candidates[t.VideoId] = (t, c.Score + 1.0 / (1 + 0.12 * rank));
            }
        }
        var ranked = candidates.Values
            .Select(c =>
            {
                var s = c.Score * (1 + 0.8 * taste.ArtistAffinity(c.Track));
                if (taste.HeardWithin(c.Track.VideoId, TimeSpan.FromHours(36))) s *= 0.15;
                if (jitter) s *= 0.6 + 0.8 * random.NextDouble();
                return (c.Track, Score: s);
            })
            .OrderByDescending(x => x.Score).Select(x => x.Track).ToList();
        var fresh = new Queue<Track>(Taste.CapPerArtist(ranked.Where(t => !taste.Knows(t.VideoId)), 2));

        var familiarPool = taste.Lately()
            .Where(e => !shown.Contains(e.Track.VideoId) && !taste.HeardWithin(e.Track.VideoId, TimeSpan.FromHours(20)))
            .Take(25).Select(e => (e.Track, Key: e.Lately * (0.5 + random.NextDouble()))).OrderByDescending(x => x.Key).Select(x => x.Track);
        var familiar = new Queue<Track>(Taste.CapPerArtist(familiarPool, 1).Take(QuickFamiliar));
        // songs you know that the radios suggested also count as familiar
        foreach (var t in ranked.Where(t => taste.Knows(t.VideoId) && !taste.HeardWithin(t.VideoId, TimeSpan.FromHours(20)) && familiar.All(f => f.VideoId != t.VideoId)).Take(2))
            familiar.Enqueue(t);

        var picks = new List<Track>();
        var perArtist = new Dictionary<string, int>();
        bool Fits(Track t)
        {
            if (picks.Any(p => p.VideoId == t.VideoId)) return false;
            var key = t.Artists.Count > 0 ? Taste.ArtistKey(t.Artists[0]) : t.VideoId;
            if (perArtist.GetValueOrDefault(key) >= 2) return false;
            perArtist[key] = perArtist.GetValueOrDefault(key) + 1;
            return true;
        }
        while (picks.Count < QuickPicksMax && (fresh.Count > 0 || familiar.Count > 0))
        {
            var wantFamiliar = picks.Count % 3 == 1 && familiar.Count > 0;
            var next = wantFamiliar || fresh.Count == 0 ? familiar.Dequeue() : fresh.Dequeue();
            if (Fits(next)) picks.Add(next);
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
