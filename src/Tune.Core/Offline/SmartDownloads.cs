using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.Offline;

/// <summary>
/// Smart downloads (like YouTube Music's): keeps up to N songs ready offline without you choosing them — mostly what
/// you play often and recently plus liked songs, topped up with ~20% similar songs from radio. Songs it picked are
/// swapped out when your taste moves on; songs you downloaded yourself are never touched.
/// </summary>
public sealed class SmartDownloads(LibraryStore lib, IYtMusic api, DownloadManager downloads, IClock clock)
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(90);
    private readonly SemaphoreSlim _one = new(1, 1);

    public bool Enabled
    {
        get => lib.GetSetting("smart.enabled") != "0";
        set => lib.SetSetting("smart.enabled", value ? "1" : "0");
    }

    public int Target
    {
        get => int.TryParse(lib.GetSetting("smart.count"), out var n) && n > 0 ? n : 100;
        set => lib.SetSetting("smart.count", value.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public DateTimeOffset? LastRun =>
        long.TryParse(lib.GetSetting("smart.lastRun"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    public int SmartCount => downloads.SmartIds().Count;

    /// <summary>Once a day is enough.</summary>
    public bool IsDue => LastRun is not { } last || clock.Now - last > TimeSpan.FromHours(20);

    /// <summary>Picks the set and queues/removes downloads to match it. Returns (added, removed).</summary>
    public async Task<(int Added, int Removed)> RefreshAsync(CancellationToken ct = default)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!Enabled)
            {
                var old = downloads.SmartIds();
                downloads.Remove(old);
                return (0, old.Count);
            }
            var picks = await PickAsync(ct).ConfigureAwait(false);
            var want = picks.Select(t => t.VideoId).ToHashSet();
            var stale = downloads.SmartIds().Where(id => !want.Contains(id)).ToList();
            downloads.Remove(stale);
            var added = downloads.Enqueue(picks, smart: true);
            lib.SetSetting("smart.lastRun", clock.Now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            Log.Info($"Smart downloads: {picks.Count} picked, {added} new, {stale.Count} swapped out");
            return (added, stale.Count);
        }
        finally
        {
            _one.Release();
        }
    }

    private async Task<IReadOnlyList<Track>> PickAsync(CancellationToken ct)
    {
        var now = clock.Now;
        var target = Target;
        var favourites = SmartPicker.Rank(lib.GetPlayStats(now - Window), lib.GetLiked(), now)
            .Take(Math.Max(1, (int)(target * 0.8))).ToList();
        var chosen = new Dictionary<string, Track>();
        var order = new List<Track>();
        void Add(Track t)
        {
            if (chosen.Count < target && chosen.TryAdd(t.VideoId, t)) order.Add(t);
        }
        favourites.ForEach(Add);
        // discovery: songs like your top ones, so offline doesn't only mean repeats
        foreach (var seed in favourites.Take(5))
        {
            if (chosen.Count >= target) break;
            IReadOnlyList<Track> radio;
            try { radio = await api.GetRadioAsync(seed.VideoId, ct).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException) { Log.Warn($"Smart downloads: radio for {seed.VideoId} failed", e); continue; }
            foreach (var t in radio.Skip(1).Take(8)) Add(t);
        }
        return order;
    }
}

/// <summary>Pure scoring, so it can be tested: how much you like a song from plays, recency and likes.</summary>
public static class SmartPicker
{
    public static IReadOnlyList<Track> Rank(IEnumerable<(Track Track, int Plays, DateTimeOffset LastPlayed)> stats,
        IEnumerable<Track> liked, DateTimeOffset now)
    {
        var score = new Dictionary<string, double>();
        var tracks = new Dictionary<string, Track>();
        foreach (var (t, plays, last) in stats)
        {
            var days = (now - last).TotalDays;
            // plays count, but a song you played a lot months ago matters less than this week's
            score[t.VideoId] = plays * (0.35 + Math.Exp(-days / 21.0));
            tracks[t.VideoId] = t;
        }
        foreach (var t in liked)
        {
            score[t.VideoId] = score.GetValueOrDefault(t.VideoId) + 2.5;
            tracks.TryAdd(t.VideoId, t);
        }
        return score.OrderByDescending(kv => kv.Value).Select(kv => tracks[kv.Key]).ToList();
    }
}
