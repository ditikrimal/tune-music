using System.Text.Json.Nodes;
using Tune.Core.Lyrics;
using Tune.Core.YtMusic;

namespace Tune.Core.Tests;

public class LyricsTests
{
    [Fact]
    public void Finds_the_lyrics_page_id_in_a_watch_next_response() =>
        Assert.Equal("MPLYt_85hsfBYAJDo-1", LyricsParser.LyricsBrowseId(Fixture.Load("next_radio.json")));

    [Fact]
    public void No_lyrics_tab_means_no_id() =>
        Assert.Null(LyricsParser.LyricsBrowseId(JsonNode.Parse("""{"contents":{}}""")!));

    [Fact]
    public void Parses_plain_lyrics_and_their_source()
    {
        var l = LyricsParser.Parse(Fixture.Load("lyrics.json"));
        Assert.NotNull(l);
        Assert.StartsWith("सुबह सुबह", l!.Text);
        Assert.Contains('\n', l.Text);
        Assert.Equal("Source: LyricFind", l.Source);
        Assert.Null(l.Lines);
    }

    [Fact]
    public void Lyrics_page_without_text_is_null() =>
        Assert.Null(LyricsParser.Parse(JsonNode.Parse("""{"contents":{"sectionListRenderer":{"contents":[]}}}""")!));

    [Fact]
    public void Lrc_lines_are_timed_sorted_and_skip_tags()
    {
        var lines = Lrc.Parse("[ar:Someone]\n[01:02.50]World\n[00:12.34]Hello\n[00:15.00]\n[00:20.123] Spaced ");
        Assert.Equal(
            [(12.34, "Hello"), (15.0, ""), (20.123, "Spaced"), (62.5, "World")],
            lines.Select(l => (Math.Round(l.At.TotalSeconds, 3), l.Text)));
    }

    [Fact]
    public void Lrc_line_with_several_stamps_repeats()
    {
        var lines = Lrc.Parse("[00:01.00][00:05.00]Chorus");
        Assert.Equal([1.0, 5.0], lines.Select(l => l.At.TotalSeconds));
        Assert.All(lines, l => Assert.Equal("Chorus", l.Text));
    }

    [Fact]
    public void Current_line_index_follows_position()
    {
        var lines = Lrc.Parse("[00:10.00]a\n[00:20.00]b\n[00:30.00]c");
        Assert.Equal(-1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Lrc.IndexAt(lines, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, Lrc.IndexAt(lines, TimeSpan.FromSeconds(29.9)));
        Assert.Equal(2, Lrc.IndexAt(lines, TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void LrcLib_response_prefers_synced_lyrics()
    {
        var l = LrcLibClient.ParseResponse("""{"plainLyrics":"Hello\nWorld","syncedLyrics":"[00:01.00]Hello\n[00:02.00]World"}""");
        Assert.NotNull(l);
        Assert.Equal("Hello\nWorld", l!.Text);
        Assert.Equal(2, l.Lines!.Count);
        Assert.Equal("Synced lyrics: LRCLIB", l.Source);
    }

    [Fact]
    public void LrcLib_instrumental_or_empty_is_null()
    {
        Assert.Null(LrcLibClient.ParseResponse("""{"instrumental":true,"plainLyrics":null,"syncedLyrics":null}"""));
        Assert.Null(LrcLibClient.ParseResponse("""{"plainLyrics":"","syncedLyrics":""}"""));
    }

    [Fact]
    public void LrcLib_query_cleans_youtube_titles()
    {
        Assert.Equal("As It Was", LrcLibClient.CleanTitle("As It Was (Official Video)"));
        Assert.Equal("Jamaican (Bam Bam)", LrcLibClient.CleanTitle("Jamaican (Bam Bam)"));
        Assert.Equal("Riptide", LrcLibClient.CleanTitle("Riptide [Official Audio]"));
        Assert.Equal("Iris", LrcLibClient.CleanTitle("Iris (Official Music Video) [4K]"));
    }
}
