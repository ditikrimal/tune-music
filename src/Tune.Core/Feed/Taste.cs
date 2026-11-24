using Tune.Core.Models;

namespace Tune.Core.Feed;

/// <summary>
/// What you like, from your own listening: every play counts, recent ones more (half-life 30 days for "lately",
/// 120 days for "all-time"); a like is worth a few plays; early skips count against a song. Artists add up
/// their songs. All Home recommendations (speed dial, quick picks, because-you-played, forgotten favourites)
/// are ranked with this.
/// </summary>
public sealed class Taste
{
    public sealed record Entry(Track Track, double Lately, double AllTime, int Plays, DateTimeOffset? LastPlayed, int Skips, bool Liked, double Now = 0);

    private const double LatelyHalfLife = 30, AllTimeHalfLife = 120, NowHalfLife = 2, LikeBonus = 2.5, SkipPenalty = 0.6;
    private readonly Dictionary<string, Entry> _songs = [];
    private readonly Dictionary<string, double> _artists = [];
    private readonly double _topArtist;
    private readonly DateTimeOffset _now;

    public Taste(IEnumerable<(Track Track, DateTimeOffset At)> plays, IEnumerable<Track> liked,
        IEnumerable<(string VideoId, DateTimeOffset At)> skips, DateTimeOffset now, IReadOnlySet<string>? notInterested = null)
    {
        notInterested ??= new HashSet<string>();
        plays = plays.Where(p => !notInterested.Contains(p.Track.VideoId));
        _now = now;
        var acc = new Dictionary<string, (Track T, double L, double A, int P, DateTimeOffset? Last, int S, bool Like, double N)>();
        foreach (var (t, at) in plays)
        {
            var e = acc.GetValueOrDefault(t.VideoId, (t, 0, 0, 0, null, 0, false, 0));
            var days = Math.Max(0, (now - at).TotalDays);
            e.L += Decay(days, LatelyHalfLife);
            e.N += Decay(days, NowHalfLife);
            e.A += Decay(days, AllTimeHalfLife);
            e.P++;
            if (e.Last is null || at > e.Last) e.Last = at;
            acc[t.VideoId] = e;
        }
        foreach (var t in liked)
        {
            var e = acc.GetValueOrDefault(t.VideoId, (t, 0, 0, 0, null, 0, false, 0));
            e.L += LikeBonus; e.A += LikeBonus; e.Like = true;
            acc[t.VideoId] = e;
        }
        foreach (var (id, at) in skips)
        {
            if (!acc.TryGetValue(id, out var e)) continue; // skipped songs we know nothing else about are handled by Skipped()
            var days = Math.Max(0, (now - at).TotalDays);
            e.L -= SkipPenalty * Decay(days, LatelyHalfLife);
            e.A -= SkipPenalty * Decay(days, AllTimeHalfLife);
            e.N -= SkipPenalty * Decay(days, NowHalfLife);
            e.S++;
            acc[id] = e;
        }
        SkippedIds = skips.Where(s => (now - s.At).TotalDays < 60).GroupBy(s => s.VideoId)
            .Where(g => g.Count() >= 2 && (!acc.TryGetValue(g.Key, out var e) || (e.P <= 1 && !e.Like)))
            .Select(g => g.Key).Concat(notInterested).ToHashSet();
        foreach (var (id, e) in acc)
        {
            _songs[id] = new Entry(e.T, e.L, e.A, e.P, e.Last, e.S, e.Like, e.N);
            foreach (var a in e.T.Artists)
                _artists[ArtistKey(a)] = _artists.GetValueOrDefault(ArtistKey(a)) + Math.Max(0, e.L + e.A * 0.3);
        }
        _topArtist = _artists.Count == 0 ? 1 : Math.Max(1e-6, _artists.Values.Max());
    }

    /// <summary>Songs skipped repeatedly and never really played: kept out of recommendations.</summary>
    public IReadOnlySet<string> SkippedIds { get; }

    public bool IsEmpty => _songs.Count == 0;

    public Entry? Get(string videoId) => _songs.GetValueOrDefault(videoId);

    /// <summary>You've played or liked this song before.</summary>
    public bool Knows(string videoId) => _songs.ContainsKey(videoId);

    public bool HeardWithin(string videoId, TimeSpan span) => _songs.GetValueOrDefault(videoId)?.LastPlayed is { } last && _now - last < span;

    /// <summary>0..1: how much you like the song's artists (1 = your top artist).</summary>
    public double ArtistAffinity(Track t) =>
        t.Artists.Count == 0 ? 0 : t.Artists.Max(a => _artists.GetValueOrDefault(ArtistKey(a))) / _topArtist;

    /// <summary>What you play lately, best first (likes included, skips subtracted).</summary>
    public IReadOnlyList<Entry> Lately() => _songs.Values.Where(e => e.Lately > 0.05).OrderByDescending(e => e.Lately).ToList();

    /// <summary>Speed dial: the songs you come back to (lately + all-time), a few per artist at most.</summary>
    public IReadOnlyList<Track> SpeedDial(int max, int perArtist = 3) =>
        CapPerArtist(_songs.Values.Where(e => (e.Plays >= 2 || e.Liked) && e.Lately + e.AllTime > 0.3)
            .OrderByDescending(e => e.Lately * 0.6 + e.AllTime * 0.4).Select(e => e.Track), perArtist).Take(max).ToList();

    /// <summary>Songs you played a lot but not in the last three weeks (Spotify's "forgotten favourites").</summary>
    public IReadOnlyList<Track> Forgotten(int max) =>
        CapPerArtist(_songs.Values.Where(e => e.Plays >= 3 && e.LastPlayed is { } last && _now - last > TimeSpan.FromDays(21) && e.AllTime > 0.5)
            .OrderByDescending(e => e.AllTime).Select(e => e.Track), 2).Take(max).ToList();

    /// <summary>Your top artists (with ids), best first.</summary>
    public IReadOnlyList<ArtistRef> TopArtists(int max) =>
        _songs.Values.SelectMany(e => e.Track.Artists).Where(a => a.BrowseId is not null)
            .GroupBy(ArtistKey).OrderByDescending(g => _artists.GetValueOrDefault(g.Key)).Select(g => g.First())
            .Where(a => _artists.GetValueOrDefault(ArtistKey(a)) > 0.3).Take(max).ToList();

    /// <summary>What you're into these days (plays of the last few days count most), best first.</summary>
    public IReadOnlyList<Entry> RightNow() =>
        _songs.Values.Where(e => e.Now > 0.05).OrderByDescending(e => e.Now).ToList();

    /// <summary>
    /// Songs to start radios from: weighted by how much you like them lately, and much more by what you played in the
    /// last few days (so Home follows the mood you're in), so seeds vary day to day.
    /// </summary>
    public IReadOnlyList<Track> Seeds(int count, Random random)
    {
        static double W(Entry e) => Math.Max(0.01, e.Lately + 2.5 * Math.Max(0, e.Now));
        var pool = _songs.Values.Where(e => e.Lately > 0.05 && !SkippedIds.Contains(e.Track.VideoId))
            .OrderByDescending(W).Take(20).ToList();
        var chosen = new List<Track>();
        while (chosen.Count < count && pool.Count > 0)
        {
            var total = pool.Sum(W);
            var roll = random.NextDouble() * total;
            var i = 0;
            for (var acc = W(pool[0]); acc < roll && i < pool.Count - 1; acc += W(pool[++i])) { }
            chosen.Add(pool[i].Track);
            pool.RemoveAt(i);
        }
        return chosen;
    }

    public static string ArtistKey(ArtistRef a) => a.BrowseId ?? a.Name.ToLowerInvariant();

    /// <summary>Keeps order, at most <paramref name="perArtist"/> songs per (main) artist.</summary>
    public static IEnumerable<Track> CapPerArtist(IEnumerable<Track> tracks, int perArtist)
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in tracks)
        {
            var key = t.Artists.Count > 0 ? ArtistKey(t.Artists[0]) : t.VideoId;
            var n = counts.GetValueOrDefault(key);
            if (n >= perArtist) continue;
            counts[key] = n + 1;
            yield return t;
        }
    }

    private static double Decay(double days, double halfLife) => Math.Pow(0.5, days / halfLife);
}
