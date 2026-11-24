using System.Text.Json.Nodes;
using Tune.Core.Library;
using Tune.Core.Models;
using Tune.Core.Sync;

namespace Tune.Core.Feed;

/// <summary>The rows Home can show. Order and visibility are the user's (Settings > Home), synced with the account.</summary>
public enum HomeSection { SpeedDial, QuickPicks, Because, Forgotten, Artists, Recent, Discovery, Vibe, Mixes }

public sealed record HomeSectionState(HomeSection Section, bool Visible);

/// <summary>Saved as "speeddial,quickpicks,…" in setting home.layout; a "-" in front hides a section.</summary>
public static class HomeLayout
{
    public const string Key = "home.layout";

    public static readonly IReadOnlyList<HomeSectionState> Default =
    [
        new(HomeSection.SpeedDial, true),
        new(HomeSection.QuickPicks, true),
        new(HomeSection.Vibe, true),
        new(HomeSection.Mixes, true),
        new(HomeSection.Because, true),
        new(HomeSection.Forgotten, true),
        new(HomeSection.Artists, true),
        new(HomeSection.Recent, false),
        new(HomeSection.Discovery, true),
    ];

    public static string Name(HomeSection s) => s switch
    {
        HomeSection.SpeedDial => "Speed dial",
        HomeSection.QuickPicks => "Quick picks",
        HomeSection.Vibe => "Keep the vibe going",
        HomeSection.Mixes => "Mood mixes",
        HomeSection.Because => "Because you played",
        HomeSection.Forgotten => "Forgotten favourites",
        HomeSection.Artists => "Your top artists",
        HomeSection.Recent => "Recently played",
        _ => "From YouTube Music",
    };

    public static string Describe(HomeSection s) => s switch
    {
        HomeSection.SpeedDial => "Your pins and most-played songs",
        HomeSection.QuickPicks => "New songs you'll like, mixed with favourites",
        HomeSection.Vibe => "More of what you just listened to",
        HomeSection.Mixes => "Your songs by mood, with new ones",
        HomeSection.Because => "More like songs you play a lot",
        HomeSection.Forgotten => "Old favourites you haven't played lately",
        HomeSection.Artists => "The artists you play most",
        HomeSection.Recent => "What you just played",
        _ => "Charts, moods and new releases",
    };

    public static IReadOnlyList<HomeSectionState> Parse(string? text)
    {
        var list = new List<HomeSectionState>();
        foreach (var raw in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hidden = raw.StartsWith('-');
            if (Enum.TryParse<HomeSection>(raw.TrimStart('-'), ignoreCase: true, out var s) && list.All(x => x.Section != s))
                list.Add(new HomeSectionState(s, !hidden));
        }
        // sections added in later versions (or a corrupt value): append in their default spot
        foreach (var d in Default.Where(d => list.All(x => x.Section != d.Section)))
            list.Insert(Math.Min(list.Count, Default.ToList().IndexOf(d)), d);
        return list;
    }

    public static string Format(IEnumerable<HomeSectionState> layout) =>
        string.Join(',', layout.Select(s => (s.Visible ? "" : "-") + s.Section.ToString().ToLowerInvariant()));

    public static IReadOnlyList<HomeSectionState> Load(LibraryStore lib) => Parse(lib.GetSetting(Key));

    public static void Save(LibraryStore lib, IEnumerable<HomeSectionState> layout) => lib.SetSetting(Key, Format(layout));
}

/// <summary>
/// Things pinned to the speed dial (songs, albums, playlists — yours or YouTube Music's), first in the grid.
/// Saved as JSON in setting speeddial.pins (synced). Your own playlists are pinned by their uid ("tune:{uid}").
/// </summary>
public static class SpeedDialPins
{
    public const string Key = "speeddial.pins";
    public const int Max = 27;

    public static string PinKey(Card c) => c.Kind switch
    {
        CardKind.Song => "song:" + c.VideoId,
        CardKind.Artist => "artist:" + c.BrowseId,
        CardKind.Album => "album:" + (c.BrowseId ?? c.PlaylistId),
        _ => "playlist:" + (c.PlaylistId ?? c.BrowseId),
    };

    public static IReadOnlyList<Card> Load(LibraryStore lib)
    {
        try
        {
            if (JsonNode.Parse(lib.GetSetting(Key) ?? "[]") is not JsonArray a) return [];
            return a.OfType<JsonObject>().Select(FromJson).OfType<Card>().ToList();
        }
        catch (System.Text.Json.JsonException) { return []; }
    }

    public static bool IsPinned(LibraryStore lib, Card c) => Load(lib).Any(p => PinKey(p) == PinKey(c));

    /// <summary>Pins (newest first) or unpins. Returns the new state.</summary>
    public static bool Toggle(LibraryStore lib, Card c)
    {
        var pins = Load(lib).ToList();
        var key = PinKey(c);
        var was = pins.RemoveAll(p => PinKey(p) == key) > 0;
        if (!was) pins.Insert(0, c);
        Save(lib, pins.Take(Max));
        return !was;
    }

    public static void Save(LibraryStore lib, IEnumerable<Card> pins) =>
        lib.SetSetting(Key, new JsonArray(pins.Select(p => (JsonNode)ToJson(p)).ToArray()).ToJsonString());

    /// <summary>A pin for one of your own playlists (works on every device: by uid, not the local row id).</summary>
    public static Card LocalPlaylistCard(string uid, string name, string? thumbnail) =>
        new(CardKind.Playlist, name, "Your playlist", thumbnail, null, null, "tune:" + uid, null);

    private static JsonObject ToJson(Card c)
    {
        var o = new JsonObject { ["k"] = c.Kind.ToString(), ["t"] = c.Title, ["s"] = c.Subtitle, ["th"] = c.ThumbnailUrl,
            ["v"] = c.VideoId, ["b"] = c.BrowseId, ["p"] = c.PlaylistId };
        if (c.Track is { } t) o["tr"] = SyncFormat.ToJson(t);
        return o;
    }

    private static Card? FromJson(JsonObject o)
    {
        if (!Enum.TryParse<CardKind>(o["k"]?.GetValue<string>(), out var kind)) return null;
        string? S(string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        return new Card(kind, S("t") ?? "", S("s"), S("th"), S("v"), S("b"), S("p"), SyncFormat.FromJson(o["tr"]));
    }
}
