using Tune.Core.Models;
using Tune.Core.Playback;

namespace Tune.Core.Tests;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset Now { get; set; } = now;
}

public sealed class FakeYtMusic : IYtMusic
{
    public Dictionary<string, IReadOnlyList<Track>> Radios { get; } = [];
    public Dictionary<string, int> RadioCalls { get; } = [];
    public IReadOnlyList<Shelf> Home { get; set; } = [new Shelf("Trending", [new Card(CardKind.Playlist, "Top 50", null, null, null, "VLx", "x", null)])];
    public Exception? HomeError { get; set; }
    public HashSet<string> FailingRadios { get; } = [];

    public Task<IReadOnlyList<Shelf>> GetHomeAsync(CancellationToken ct = default) =>
        HomeError is null ? Task.FromResult(Home) : Task.FromException<IReadOnlyList<Shelf>>(HomeError);

    public Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, CancellationToken ct = default)
    {
        RadioCalls[videoId] = RadioCalls.GetValueOrDefault(videoId) + 1;
        if (FailingRadios.Contains(videoId)) return Task.FromException<IReadOnlyList<Track>>(new HttpRequestException("boom"));
        return Task.FromResult(Radios.GetValueOrDefault(videoId) ?? (IReadOnlyList<Track>)[]);
    }

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Collection> GetAlbumAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Collection> GetPlaylistAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
}
