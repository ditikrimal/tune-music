using Tune.Core.Models;

namespace Tune.Core.Playback;

public interface IClock
{
    DateTimeOffset Now { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset Now => DateTimeOffset.Now;
}

public interface IAudioPlayer
{
    Task LoadAsync(Uri url, TimeSpan startAt);
    void Play();
    void Pause();
    void Stop();
    void Seek(TimeSpan pos);
    double Volume { get; set; }

    /// <summary>Evens out loud and quiet songs (dynamic audio normalisation).</summary>
    bool Normalize { get; set; }
    TimeSpan Position { get; }
    TimeSpan? Duration { get; }
    bool IsPaused { get; }

    /// <summary>The song reached its end.</summary>
    event Action? Ended;

    /// <summary>The stream could not be opened or broke mid-way.</summary>
    event Action<string>? Failed;

    /// <summary>Raised a few times per second while playing.</summary>
    event Action? PositionChanged;
}

public interface IYtMusic
{
    Task<SearchResults> SearchAsync(string query, CancellationToken ct = default);
    Task<IReadOnlyList<Shelf>> GetHomeAsync(CancellationToken ct = default);
    Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, CancellationToken ct = default);
    Task<Collection> GetAlbumAsync(string browseId, CancellationToken ct = default);
    Task<Collection> GetPlaylistAsync(string playlistId, CancellationToken ct = default);
    Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default);

    /// <summary>A "See all" grid (an artist's albums, singles…).</summary>
    Task<GridPage> GetGridAsync(BrowseLink link, CancellationToken ct = default) => Task.FromResult(new GridPage("", []));

    /// <summary>YouTube Music's Moods &amp; genres tiles.</summary>
    Task<IReadOnlyList<MoodSection>> GetMoodsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<MoodSection>>([]);

    /// <summary>One mood or genre: rows of playlists.</summary>
    Task<IReadOnlyList<Shelf>> GetMoodAsync(string moodParams, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Shelf>>([]);
}
