using System.Text.RegularExpressions;

namespace Tune.Core.YtMusic;

/// <summary>Finds the playlist id in whatever the user pasted: a music.youtube.com / youtube.com link or a bare id.</summary>
public static partial class PlaylistLinks
{
    [GeneratedRegex(@"[?&]list=([A-Za-z0-9_-]{10,})")]
    private static partial Regex ListParam();

    [GeneratedRegex(@"^(VL)?(PL|OLAK5uy_|RD|LL|FL|UU)[A-Za-z0-9_-]{8,}$")]
    private static partial Regex BareId();

    public static string? Parse(string? text)
    {
        var t = text?.Trim() ?? "";
        if (ListParam().Match(t) is { Success: true } m) return m.Groups[1].Value;
        return BareId().IsMatch(t) ? (t.StartsWith("VL", StringComparison.Ordinal) ? t[2..] : t) : null;
    }
}
