using System.Collections.Concurrent;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.Feed;

/// <summary>
/// YouTube Music's own knowledge of each song (its radio, vibe chips, Related tab), fetched once and cached in the
/// library for a few days. Concurrent asks for the same song share one request.
/// </summary>
public sealed class Signals(LibraryStore lib, IYtMusic api, IClock clock)
{
    private static readonly TimeSpan FreshFor = TimeSpan.FromDays(3);
    private static readonly TimeSpan TunedFor = TimeSpan.FromHours(6);
    private static readonly TimeSpan RelatedFor = TimeSpan.FromHours(12);

    private readonly ConcurrentDictionary<string, SongSignals> _mem = new();
    private readonly ConcurrentDictionary<string, Task<SongSignals?>> _inflight = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<Track> Tracks)> _tuned = new();
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RelatedPage Page)> _related = new();

    public IYtMusic Api => api;

    /// <summary>Whatever is known about the song, however old (no network).</summary>
    public SongSignals? Cached(string videoId)
    {
        if (_mem.TryGetValue(videoId, out var hit)) return hit;
        if (lib.GetSignals(videoId) is { } s) return _mem[videoId] = s;
        return null;
    }

    /// <summary>The song's radio and chips: cached if fresh, else from YouTube Music (old data if that fails).</summary>
    public Task<SongSignals?> GetAsync(string videoId, bool fresh = false, CancellationToken ct = default)
    {
        if (!fresh && Cached(videoId) is { } s && clock.Now - s.At < FreshFor) return Task.FromResult<SongSignals?>(s);
        return _inflight.GetOrAdd(videoId, id => FetchAsync(id, ct));
    }

    private async Task<SongSignals?> FetchAsync(string videoId, CancellationToken ct)
    {
        try
        {
            var page = await api.GetRadioPageAsync(videoId, null, ct).ConfigureAwait(false);
            if (page.Tracks.Count == 0) return Cached(videoId);
            var s = new SongSignals(videoId, page.Tracks, page.Chips, page.RelatedId, page.Continuation, clock.Now);
            _mem[videoId] = s;
            lib.SaveSignals(s);
            return s;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Signals for {videoId} unavailable", e);
            return Cached(videoId);
        }
        finally
        {
            _inflight.TryRemove(videoId, out _);
        }
    }

    /// <summary>The song's radio tuned with one of its chips (e.g. only the "Chill" side of it).</summary>
    public async Task<IReadOnlyList<Track>> TunedAsync(string videoId, VibeChip chip, CancellationToken ct = default)
    {
        var key = videoId + "|" + chip.Name;
        if (_tuned.TryGetValue(key, out var hit) && clock.Now - hit.At < TunedFor) return hit.Tracks;
        try
        {
            var page = await api.GetRadioPageAsync(videoId, chip, ct).ConfigureAwait(false);
            _tuned[key] = (clock.Now, page.Tracks);
            return page.Tracks;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Tuned radio {chip.Name} for {videoId} unavailable", e);
            return hit.Tracks ?? [];
        }
    }

    /// <summary>The song's "You might also like", similar artists and playlists.</summary>
    public async Task<RelatedPage?> RelatedAsync(string videoId, CancellationToken ct = default)
    {
        if (_related.TryGetValue(videoId, out var hit) && clock.Now - hit.At < RelatedFor) return hit.Page;
        if (await GetAsync(videoId, ct: ct).ConfigureAwait(false) is not { RelatedId: { } rid }) return null;
        try
        {
            var page = await api.GetRelatedAsync(rid, ct).ConfigureAwait(false);
            _related[videoId] = (clock.Now, page);
            return page;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Warn($"Related for {videoId} unavailable", e);
            return hit.Page;
        }
    }
}

/// <summary>One song of a listening session: <paramref name="Weight"/> &gt; 0 for songs you let play (newest weighs most),
/// &lt; 0 for songs you skipped.</summary>
public sealed record SessionSong(Track Track, double Weight, DateTimeOffset At)
{
    public bool Skipped => Weight < 0;
}

/// <summary>
/// What you're listening to right now: the run of plays and skips without a long break, newest first. Songs you
/// let play pull recommendations towards them (the latest most), skipped songs push away. Its <see cref="Mood"/> is a
/// vibe chip most of the songs you let play share ("Downbeat", "Pump-up"…).
/// </summary>
public sealed class Session
{
    public static readonly TimeSpan Gap = TimeSpan.FromMinutes(45);
    private const double Fade = 0.65, SkipWeight = 0.7;
    private const int MaxSongs = 6, MaxSkips = 4;

    public Session(IReadOnlyList<SessionSong> songs) => Songs = songs;

    public IReadOnlyList<SessionSong> Songs { get; }

    public IEnumerable<SessionSong> Played => Songs.Where(s => !s.Skipped);

    public IEnumerable<SessionSong> SkippedSongs => Songs.Where(s => s.Skipped);

    public bool IsEmpty => !Played.Any();

    /// <param name="current">The song playing now (it leads the session even before it counts as a play).</param>
    /// <param name="track">Looks up a skipped song (skips only store the id).</param>
    /// <param name="maxAge">How long ago the session may have ended (Home's "keep the vibe" looks back further than the queue).</param>
    public static Session Build(IEnumerable<(Track Track, DateTimeOffset At)> plays, IEnumerable<(string VideoId, DateTimeOffset At)> skips,
        Func<string, Track?> track, DateTimeOffset now, Track? current = null, TimeSpan? maxAge = null)
    {
        var events = plays.Select(p => (p.Track.VideoId, Track: (Track?)p.Track, p.At, Skip: false))
            .Concat(skips.Select(s => (s.VideoId, Track: (Track?)null, s.At, Skip: true)))
            .OrderByDescending(e => e.At).ToList();
        var songs = new List<SessionSong>();
        var seen = new HashSet<string>();
        int played = 0, skipped = 0;
        if (current is not null)
        {
            songs.Add(new SessionSong(current, 1, now));
            seen.Add(current.VideoId);
            played = 1;
        }
        var last = now;
        var first = true;
        foreach (var e in events)
        {
            // a break ends the session; with nothing playing, the latest session may be up to maxAge old
            var gap = last - e.At;
            if (first && current is null ? gap > (maxAge ?? Gap) : gap > Gap) break;
            first = false;
            last = e.At;
            if (!seen.Add(e.VideoId)) continue;
            if (e.Skip)
            {
                if (skipped >= MaxSkips || (e.Track ?? track(e.VideoId)) is not { } t) continue;
                songs.Add(new SessionSong(t, -SkipWeight * Math.Pow(Fade, skipped + played * 0.5), e.At));
                skipped++;
            }
            else if (played < MaxSongs)
            {
                songs.Add(new SessionSong(e.Track!, Math.Pow(Fade, played), e.At));
                played++;
            }
            if (played >= MaxSongs && skipped >= MaxSkips) break;
        }
        return new Session(songs);
    }

    /// <summary>
    /// The vibe chip most songs you let play share, if any: at least two songs have it and it carries close to half the
    /// session's weight. Skipped songs with the chip count against it. <paramref name="kind"/>: moods first, else genres.
    /// </summary>
    public string? Mood(Func<string, IReadOnlyList<VibeChip>?> chips, VibeKind kind = VibeKind.Mood)
    {
        var total = Played.Sum(s => s.Weight);
        if (total <= 0) return null;
        var score = new Dictionary<string, (double Score, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Songs)
        {
            if (chips(s.Track.VideoId) is not { } list) continue;
            foreach (var c in list.Where(c => c.Kind == kind))
            {
                var e = score.GetValueOrDefault(c.Name);
                score[c.Name] = (e.Score + (s.Skipped ? s.Weight * 0.5 : s.Weight), e.Count + (s.Skipped ? 0 : 1));
            }
        }
        return score.Where(kv => kv.Value.Count >= 2 && kv.Value.Score >= 0.45 * total)
            .OrderByDescending(kv => kv.Value.Score * Specificity(kv.Key)).Select(kv => kv.Key).FirstOrDefault();
    }

    // YouTube tags many songs "Romance", "Party", "Upbeat" or "Chill"; a shared "Pump-up" or "Downbeat" says much more
    private static readonly HashSet<string> Broad = new(StringComparer.OrdinalIgnoreCase) { "Romance", "Party", "Upbeat", "Chill", "Feel good", "Love", "Dance" };

    private static double Specificity(string chip) => Broad.Contains(chip) ? 0.75 : 1.25;
}

/// <summary>
/// Ranks candidate songs the way YouTube's "Up next" feels: songs that several of your recent songs' radios agree
/// on come first (early places in a radio count more), a tuned radio for your current vibe adds to them, the
/// neighbours of songs you skipped sink, artists you love rise a little, and the same artist doesn't come twice in a row.
/// </summary>
public static class UpNext
{
    /// <summary>A list of suggestions with how much it counts (negative: steer away from these).</summary>
    public sealed record Source(IReadOnlyList<Track> Tracks, double Weight, int Take = int.MaxValue);

    private const double RankFade = 0.08;

    public static IReadOnlyList<Track> Rank(IEnumerable<Source> sources, IReadOnlySet<string> exclude, int count,
        Taste? taste = null, IReadOnlySet<string>? avoidArtists = null, Random? jitter = null, IReadOnlySet<string>? excludeTitles = null)
    {
        var scores = new Dictionary<string, (Track Track, double Score, int Votes)>();
        foreach (var src in sources)
        {
            for (var rank = 0; rank < src.Tracks.Count && rank < src.Take; rank++)
            {
                var t = src.Tracks[rank];
                if (exclude.Contains(t.VideoId)) continue;
                var e = scores.GetValueOrDefault(t.VideoId, (t, 0, 0));
                scores[t.VideoId] = (e.Track, e.Score + src.Weight / (1 + RankFade * rank), e.Votes + (src.Weight > 0 ? 1 : 0));
            }
        }
        var ranked = scores.Values
            .Where(e => e.Votes > 0)
            .Select(e =>
            {
                var s = e.Score;
                if (taste is not null) s *= 1 + 0.35 * taste.ArtistAffinity(e.Track);
                if (avoidArtists is not null && e.Track.Artists.Any(a => avoidArtists.Contains(Taste.ArtistKey(a)))) s *= 0.45;
                if (jitter is not null) s *= 0.75 + 0.5 * jitter.NextDouble();
                return (e.Track, Score: s);
            })
            .Where(e => e.Score > 0)
            .OrderByDescending(e => e.Score).Select(e => e.Track)
            // one version of a song: no live take, remix or cover of something you just heard or that's already here
            .Where(t => excludeTitles is null || !excludeTitles.Contains(TitleKey(t.Title)))
            .DistinctBy(t => TitleKey(t.Title))
            .ToList();
        return Spaced(ranked, count);
    }

    /// <summary>"Believer (Live at Pinkpop) - Remastered" and "Believer" are the same song.</summary>
    public static string TitleKey(string title)
    {
        var t = title.ToLowerInvariant();
        foreach (var (open, close) in new[] { ('(', ')'), ('[', ']'), ('{', '}') })
        {
            for (var i = t.IndexOf(open); i >= 0; i = t.IndexOf(open))
            {
                var j = t.IndexOf(close, i);
                t = j < 0 ? t[..i] : t[..i] + t[(j + 1)..];
            }
        }
        foreach (var cut in new[] { " - ", " | ", " feat.", " ft.", " ft ", " featuring ", " x " })
            if (t.IndexOf(cut, StringComparison.Ordinal) is var k and > 0) t = t[..k];
        var key = new string(t.Where(char.IsLetterOrDigit).ToArray());
        return key.Length > 0 ? key : title;
    }

    /// <summary>Keeps the order but never the same main artist within three songs (those wait a little).</summary>
    public static IReadOnlyList<Track> Spaced(IReadOnlyList<Track> ranked, int count, int window = 3)
    {
        var picks = new List<Track>();
        var waiting = new List<Track>();
        static string Key(Track t) => t.Artists.Count > 0 ? Taste.ArtistKey(t.Artists[0]) : t.VideoId;
        bool Fits(Track t) => picks.TakeLast(window).All(p => Key(p) != Key(t));
        foreach (var t in ranked)
        {
            if (picks.Count >= count) break;
            var w = waiting.FindIndex(Fits);
            if (w >= 0)
            {
                picks.Add(waiting[w]);
                waiting.RemoveAt(w);
                if (picks.Count >= count) break;
            }
            if (Fits(t)) picks.Add(t);
            else waiting.Add(t);
        }
        foreach (var t in waiting)
        {
            if (picks.Count >= count) break;
            picks.Add(t);
        }
        return picks;
    }
}

/// <summary>What the vibe engine chose: the songs, the vibe it followed (a chip name, or null) and the current song's chips.</summary>
public sealed record VibeResult(IReadOnlyList<Track> Tracks, string? Vibe, IReadOnlyList<VibeChip> Chips);

/// <summary>
/// Tune's answer to YouTube's recommendations, built from what YouTube Music says about each song: the radio keeps
/// following what you're listening to now (and away from what you skip), in the vibe your recent songs share, or in
/// the one you pick with a chip.
/// </summary>
public sealed class VibeEngine(LibraryStore lib, Signals signals, IClock clock)
{
    private const double TunedWeight = 1.3, ChosenWeight = 2.6, RelatedWeight = 0.6;

    public Signals Signals => signals;

    /// <summary>A chip the user picked for the radio ("Popular", "Chill", "2010s"…); null = follow the vibe.</summary>
    public string? Tuning { get; set; }

    public Taste LoadTaste()
    {
        var now = clock.Now;
        return new Taste(lib.GetPlays(now - TimeSpan.FromDays(365)), lib.GetLiked(), lib.GetSkips(now - TimeSpan.FromDays(90)), now, lib.GetNotInterested());
    }

    public Session CurrentSession(Track? current, TimeSpan? maxAge = null)
    {
        var now = clock.Now;
        var since = now - TimeSpan.FromHours(12);
        return Session.Build(lib.GetPlays(since), lib.GetSkips(since), id => lib.GetTrack(id) ?? signals.Cached(id)?.Radio.FirstOrDefault(),
            now, current, maxAge);
    }

    /// <summary>The next songs after <paramref name="current"/>, leaving out <paramref name="exclude"/> (the queue so far).</summary>
    public async Task<VibeResult> UpNextAsync(Track current, IEnumerable<string> exclude, int count, CancellationToken ct = default)
    {
        var session = CurrentSession(current);
        var currentSignals = await signals.GetAsync(current.VideoId, ct: ct).ConfigureAwait(false);
        var (tracks, vibe) = await RankAsync(session, exclude.ToHashSet(), count, Tuning, null, ct).ConfigureAwait(false);
        return new VibeResult(tracks, vibe, currentSignals?.Chips ?? []);
    }

    /// <summary>Ranks songs for a session (also Home's "Keep the vibe going").</summary>
    public async Task<(IReadOnlyList<Track> Tracks, string? Vibe)> RankAsync(Session session, HashSet<string> exclude, int count,
        string? tuning = null, Random? jitter = null, CancellationToken ct = default)
    {
        if (session.IsEmpty) return ([], null);
        var played = session.Played.Take(5).ToList();
        var skipped = session.SkippedSongs.Take(3).ToList();
        var all = played.Concat(skipped).ToList();
        var got = await Task.WhenAll(all.Select(s => signals.GetAsync(s.Track.VideoId, ct: ct))).ConfigureAwait(false);
        var byId = all.Select((s, i) => (s.Track.VideoId, got[i])).Where(x => x.Item2 is not null).ToDictionary(x => x.VideoId, x => x.Item2!);

        var taste = LoadTaste();
        foreach (var s in session.Songs) exclude.Add(s.Track.VideoId);
        foreach (var id in taste.SkippedIds) exclude.Add(id);
        foreach (var e in taste.Lately().Where(e => taste.HeardWithin(e.Track.VideoId, TimeSpan.FromHours(3)))) exclude.Add(e.Track.VideoId);

        // the vibe to follow: the one the user picked, else the mood (then genre) the session's songs share
        var vibe = tuning ?? session.Mood(id => byId.GetValueOrDefault(id)?.Chips) ?? session.Mood(id => byId.GetValueOrDefault(id)?.Chips, VibeKind.Genre);
        var sources = new List<UpNext.Source>();
        var chosen = tuning is not null;
        foreach (var s in played)
            if (byId.TryGetValue(s.Track.VideoId, out var sig)) sources.Add(new(sig.Radio, s.Weight * (chosen ? 0.4 : 1)));
        if (vibe is not null)
        {
            // tune the radio of the latest song that has this chip (chips are per song)
            foreach (var s in played)
            {
                if (byId.GetValueOrDefault(s.Track.VideoId)?.Chips.FirstOrDefault(c => c.Name.Equals(vibe, StringComparison.OrdinalIgnoreCase)) is not { } chip) continue;
                var tuned = await signals.TunedAsync(s.Track.VideoId, chip, ct).ConfigureAwait(false);
                sources.Add(new(tuned, chosen ? ChosenWeight : TunedWeight * Math.Max(0.5, s.Weight)));
                break;
            }
        }
        // the latest song's "You might also like" widens the pool a little
        if (!chosen && played.Count > 0 && await signals.RelatedAsync(played[0].Track.VideoId, ct).ConfigureAwait(false) is { } related)
            sources.Add(new(related.Songs, RelatedWeight));
        var avoid = new HashSet<string>();
        foreach (var s in skipped)
        {
            if (byId.TryGetValue(s.Track.VideoId, out var sig)) sources.Add(new(sig.Radio, s.Weight * 0.8, Take: 15));
            if (s.Track.Artists.Count > 0 && played.All(p => p.Track.Artists.All(a => Taste.ArtistKey(a) != Taste.ArtistKey(s.Track.Artists[0]))))
                avoid.Add(Taste.ArtistKey(s.Track.Artists[0]));
        }
        foreach (var id in lib.GetNotInterested()) exclude.Add(id);
        var titles = session.Played.Select(s => UpNext.TitleKey(s.Track.Title)).ToHashSet();
        return (UpNext.Rank(sources, exclude, count, taste, avoid, jitter, titles), vibe);
    }

    /// <summary>
    /// "Your chill mix", "Your pump-up mix"…: your own songs grouped by the moods YouTube Music tags them with, each
    /// mixed with new songs from that mood's tuned radios. Moods with at least three of your songs, strongest first.
    /// </summary>
    public async Task<IReadOnlyList<(string Mood, IReadOnlyList<Track> Tracks)>> MixesAsync(Taste taste, IReadOnlySet<string> shown, int rows, int perRow,
        Random random, CancellationToken ct = default)
    {
        var yours = taste.Lately().Where(e => !taste.SkippedIds.Contains(e.Track.VideoId)).Take(30).ToList();
        // chips we don't know yet: a few lookups per Home build fill them in over time
        var missing = yours.Where(e => signals.Cached(e.Track.VideoId) is null).Take(8).ToList();
        if (missing.Count > 0) await Task.WhenAll(missing.Select(e => signals.GetAsync(e.Track.VideoId, ct: ct))).ConfigureAwait(false);

        var moods = new Dictionary<string, List<Taste.Entry>>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in yours)
            foreach (var c in signals.Cached(e.Track.VideoId)?.Chips.Where(c => c.Kind == VibeKind.Mood) ?? [])
                (moods.TryGetValue(c.Name, out var l) ? l : moods[c.Name] = []).Add(e);

        var result = new List<(string, IReadOnlyList<Track>)>();
        var used = new HashSet<string>(shown);
        foreach (var (mood, songs) in moods.Where(m => m.Value.Count >= 3).OrderByDescending(m => m.Value.Sum(e => e.Lately)).Take(rows))
        {
            var seeds = songs.Take(3).ToList();
            var sources = new List<UpNext.Source>();
            for (var i = 0; i < seeds.Count && i < 2; i++)
            {
                if (signals.Cached(seeds[i].Track.VideoId)?.Chips.FirstOrDefault(c => c.Name.Equals(mood, StringComparison.OrdinalIgnoreCase)) is not { } chip) continue;
                sources.Add(new(await signals.TunedAsync(seeds[i].Track.VideoId, chip, ct).ConfigureAwait(false), 1.0 / (i + 1)));
            }
            var fresh = new Queue<Track>(UpNext.Rank(sources, used.Concat(yours.Select(y => y.Track.VideoId)).ToHashSet(), perRow, taste, jitter: random));
            var own = new Queue<Track>(songs.Select(e => e.Track).Where(t => !used.Contains(t.VideoId)).OrderBy(_ => random.Next()).Take(perRow / 3 + 1));
            var list = new List<Track>();
            // like YouTube's mixes: mostly new songs with one of yours every third place
            while (list.Count < perRow && (fresh.Count > 0 || own.Count > 0))
                list.Add(list.Count % 3 == 0 && own.Count > 0 || fresh.Count == 0 ? own.Dequeue() : fresh.Dequeue());
            if (list.Count < 5) continue;
            foreach (var t in list) used.Add(t.VideoId);
            result.Add((mood, list));
        }
        return result;
    }
}
