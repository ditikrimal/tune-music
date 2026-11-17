using System.Text.Json;
using System.Text.Json.Nodes;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.Core.Feed;

/// <summary>
/// Moods &amp; genres for the Search page, kept for a week (they rarely change). Each tile's picture is the cover of
/// the first playlist on its page, fetched the first time the tile is shown and remembered with the list.
/// </summary>
public sealed class MoodsCatalog(IYtMusic api, LibraryStore store, IClock clock)
{
    private const string Key = "moods.cache";
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(7);
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _covers = new(4);
    private IReadOnlyList<MoodSection>? _sections;
    private DateTimeOffset _at;

    /// <summary>The tiles: from the cache when it's fresh; on a failed refresh, the old ones.</summary>
    public async Task<IReadOnlyList<MoodSection>> GetAsync(CancellationToken ct = default)
    {
        lock (_gate) if (_sections is null) Load();
        if (_sections is { Count: > 0 } cached && clock.Now - _at < KeepFor) return cached;
        try
        {
            var fresh = await api.GetMoodsAsync(ct).ConfigureAwait(false);
            if (fresh.Count == 0) return _sections ?? [];
            lock (_gate)
            {
                // keep covers we already found for tiles that are still there
                var covers = (_sections ?? []).SelectMany(s => s.Items).Where(i => i.ThumbnailUrl is not null)
                    .GroupBy(i => i.Title).ToDictionary(g => g.Key, g => g.First().ThumbnailUrl);
                _sections = fresh.Select(s => s with { Items = s.Items.Select(i => i with { ThumbnailUrl = covers.GetValueOrDefault(i.Title) }).ToList() }).ToList();
                _at = clock.Now;
                Save();
                return _sections;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Log.Warn("Moods & genres failed", e);
            return _sections ?? [];
        }
    }

    /// <summary>A tile's picture (the cover of its first playlist); null when the page couldn't be read.</summary>
    public async Task<string?> CoverAsync(MoodCategory mood, CancellationToken ct = default)
    {
        if (mood.ThumbnailUrl is not null) return mood.ThumbnailUrl;
        await _covers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var shelves = await api.GetMoodAsync(mood.Params, ct).ConfigureAwait(false);
            var url = shelves.SelectMany(s => s.Items).Select(c => c.ThumbnailUrl).FirstOrDefault(u => u is not null);
            if (url is null) return null;
            lock (_gate)
            {
                if (_sections is not null)
                {
                    _sections = _sections.Select(s => s with { Items = s.Items.Select(i => i.Params == mood.Params ? i with { ThumbnailUrl = url } : i).ToList() }).ToList();
                    Save();
                }
            }
            return url;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            Log.Warn($"Mood cover for {mood.Title} failed", e);
            return null;
        }
        finally
        {
            _covers.Release();
        }
    }

    private void Load()
    {
        try
        {
            if (store.GetSetting(Key) is not { } text || JsonNode.Parse(text) is not JsonObject o) { _sections = []; return; }
            _at = DateTimeOffset.FromUnixTimeSeconds(o["at"]?.GetValue<long>() ?? 0);
            _sections = (o["sections"] as JsonArray ?? []).OfType<JsonObject>().Select(s => new MoodSection(
                s["title"]?.GetValue<string>() ?? "",
                (s["items"] as JsonArray ?? []).OfType<JsonObject>().Select(i => new MoodCategory(
                    i["t"]!.GetValue<string>(), i["p"]!.GetValue<string>(), i["c"]!.GetValue<uint>(), i["img"]?.GetValue<string>())).ToList()))
                .ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NullReferenceException or FormatException)
        {
            Log.Warn("Moods cache unreadable", e);
            _sections = [];
        }
    }

    private void Save()
    {
        var o = new JsonObject
        {
            ["at"] = _at.ToUnixTimeSeconds(),
            ["sections"] = new JsonArray((_sections ?? []).Select(s => (JsonNode)new JsonObject
            {
                ["title"] = s.Title,
                ["items"] = new JsonArray(s.Items.Select(i => (JsonNode)new JsonObject
                {
                    ["t"] = i.Title, ["p"] = i.Params, ["c"] = i.Color, ["img"] = i.ThumbnailUrl,
                }).ToArray()),
            }).ToArray()),
        };
        store.SetSetting(Key, o.ToJsonString());
    }
}
