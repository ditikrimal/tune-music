using System.Text.Json;
using Tune.Core.Models;

namespace Tune.Core.Library;

/// <summary>What YouTube Music says about a song: its radio, its vibe chips and its Related tab id (fetched at <paramref name="At"/>).</summary>
public sealed record SongSignals(string VideoId, IReadOnlyList<Track> Radio, IReadOnlyList<VibeChip> Chips, string? RelatedId,
    string? Continuation, DateTimeOffset At);

/// <summary>A local cache of <see cref="SongSignals"/> (not synced; rebuilt from YouTube Music when old).</summary>
public sealed partial class LibraryStore
{
    private const int SignalsKeep = 3000;

    private void MigrateSignals() =>
        Exec("""
            CREATE TABLE IF NOT EXISTS song_signals (video_id TEXT PRIMARY KEY, json TEXT NOT NULL, at INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS song_signals_at ON song_signals(at);
            """);

    /// <summary>A song Tune has seen played, liked or skipped.</summary>
    public Track? GetTrack(string videoId) =>
        Scalar("SELECT json FROM tracks WHERE video_id = $v", ("$v", videoId)) is string json ? Deserialize(json) : null;

    /// <summary>A skip that also remembers the song (so recommendations know whose song it was).</summary>
    public void RecordSkip(Track t, DateTimeOffset at)
    {
        lock (_gate)
        {
            SaveTrack(t);
            RecordSkip(t.VideoId, at);
        }
    }

    public SongSignals? GetSignals(string videoId)
    {
        if (Scalar("SELECT json FROM song_signals WHERE video_id = $v", ("$v", videoId)) is not string json) return null;
        try { return JsonSerializer.Deserialize<SongSignals>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public void SaveSignals(SongSignals s)
    {
        lock (_gate)
        {
            Exec("INSERT INTO song_signals (video_id, json, at) VALUES ($v, $j, $at) ON CONFLICT(video_id) DO UPDATE SET json = excluded.json, at = excluded.at",
                ("$v", s.VideoId), ("$j", JsonSerializer.Serialize(s, JsonOptions)), ("$at", s.At.ToUnixTimeMilliseconds()));
            if (Random.Shared.Next(50) == 0)
                Exec("DELETE FROM song_signals WHERE video_id NOT IN (SELECT video_id FROM song_signals ORDER BY at DESC LIMIT $n)", ("$n", SignalsKeep));
        }
    }
}
