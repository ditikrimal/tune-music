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

    /// <summary>Radios that only answer once the test completes their gate.</summary>
    public Dictionary<string, TaskCompletionSource> RadioGates { get; } = [];

    public Task<IReadOnlyList<Shelf>> GetHomeAsync(CancellationToken ct = default) =>
        HomeError is null ? Task.FromResult(Home) : Task.FromException<IReadOnlyList<Shelf>>(HomeError);

    public async Task<IReadOnlyList<Track>> GetRadioAsync(string videoId, CancellationToken ct = default)
    {
        RadioCalls[videoId] = RadioCalls.GetValueOrDefault(videoId) + 1;
        if (RadioGates.TryGetValue(videoId, out var gate)) await gate.Task;
        if (FailingRadios.Contains(videoId)) throw new HttpRequestException("boom");
        return Radios.GetValueOrDefault(videoId) ?? [];
    }

    /// <summary>Vibe chips per song; tuned radios keyed "videoId|chip"; Related songs per song.</summary>
    public Dictionary<string, IReadOnlyList<VibeChip>> Chips { get; } = [];
    public Dictionary<string, IReadOnlyList<Track>> Tuned { get; } = [];
    public Dictionary<string, IReadOnlyList<Track>> Related { get; } = [];
    public List<string> TunedCalls { get; } = [];

    public async Task<RadioPage> GetRadioPageAsync(string videoId, VibeChip? tuning = null, CancellationToken ct = default)
    {
        if (tuning is not null)
        {
            TunedCalls.Add(videoId + "|" + tuning.Name);
            return new RadioPage(Tuned.GetValueOrDefault(videoId + "|" + tuning.Name) ?? [], [], null);
        }
        var tracks = await GetRadioAsync(videoId, ct);
        return new RadioPage(tracks, Chips.GetValueOrDefault(videoId) ?? [], null, Related.ContainsKey(videoId) ? "MPTRt_" + videoId : null);
    }

    public Task<RelatedPage> GetRelatedAsync(string relatedId, CancellationToken ct = default) =>
        Task.FromResult(new RelatedPage(Related.GetValueOrDefault(relatedId["MPTRt_".Length..]) ?? [], [], []));

    public static VibeChip Chip(string name, string videoId = "x") =>
        new(name, "RDAT" + name + videoId, "p", Tune.Core.YtMusic.RadioParser.KindOf(name));

    public Task<SearchResults> SearchAsync(string query, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Collection> GetAlbumAsync(string browseId, CancellationToken ct = default) => throw new NotSupportedException();
    public Task<Collection> GetPlaylistAsync(string playlistId, CancellationToken ct = default) => throw new NotSupportedException();
    public Dictionary<string, string> ArtistPictures { get; } = [];
    public int ArtistCalls;

    public Task<ArtistPage> GetArtistAsync(string browseId, CancellationToken ct = default)
    {
        Interlocked.Increment(ref ArtistCalls);
        return Task.FromResult(new ArtistPage(browseId, ArtistPictures.GetValueOrDefault(browseId), [], []));
    }
}
