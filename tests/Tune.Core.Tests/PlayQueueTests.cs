using Tune.Core.Models;
using Tune.Core.Playback;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

public class PlayQueueTests
{
    private static Track[] List(params string[] ids) => ids.Select(i => T(i)).ToArray();
    private static string Id(string s) => T(s).VideoId;
    private static List<string> Ids(PlayQueue q) => q.Items.Select(t => t.VideoId).ToList();

    [Fact]
    public void List_plays_through_then_requests_radio_from_last()
    {
        var q = new PlayQueue();
        q.StartList(List("a", "b", "c"), 0);
        Assert.Equal(Id("a"), q.Current!.VideoId);
        Assert.False(q.NeedsRadio);
        Assert.Equal(Id("b"), q.Next(false)!.VideoId);
        Assert.Equal(Id("c"), q.Next(false)!.VideoId);
        Assert.True(q.NeedsRadio);
        Assert.Equal(Id("c"), q.RadioSeedId);
        Assert.Null(q.Next(false));
        Assert.Equal(Id("c"), q.Current!.VideoId); // stays put until radio arrives
        q.AppendRadio(List("c", "x", "y"));
        Assert.False(q.NeedsRadio);
        Assert.Equal(Id("x"), q.Next(false)!.VideoId);
    }

    [Fact]
    public void Start_list_mid_way_keeps_earlier_items_for_previous()
    {
        var q = new PlayQueue();
        q.StartList(List("a", "b", "c"), 1);
        Assert.Equal(Id("b"), q.Current!.VideoId);
        Assert.Equal(Id("a"), q.Previous()!.VideoId);
        Assert.Equal(Id("a"), q.Previous()!.VideoId); // already at start
    }

    [Fact]
    public void Radio_started_from_card_requests_radio_immediately()
    {
        var q = new PlayQueue();
        q.StartRadio(T("a"));
        Assert.True(q.NeedsRadio);
        Assert.Equal(Id("a"), q.RadioSeedId);
        q.AppendRadio(List("a", "x", "y"));
        Assert.Equal([Id("a"), Id("x"), Id("y")], Ids(q));
    }

    [Fact]
    public void AppendRadio_dedupes()
    {
        var q = new PlayQueue();
        q.StartList(List("a", "b"), 0);
        q.AppendRadio(List("b", "x", "a", "x", "y"));
        Assert.Equal([Id("a"), Id("b"), Id("x"), Id("y")], Ids(q));
    }

    [Fact]
    public void Repeat_all_wraps_list_no_radio()
    {
        var q = new PlayQueue { Repeat = RepeatMode.All };
        q.StartList(List("a", "b"), 0);
        q.Next(false);
        Assert.False(q.NeedsRadio);
        Assert.Equal(Id("a"), q.Next(false)!.VideoId);
        Assert.Equal(Id("b"), q.PeekNext()!.VideoId);
    }

    [Fact]
    public void Repeat_one_repeats_on_auto_advance_but_next_button_advances()
    {
        var q = new PlayQueue { Repeat = RepeatMode.One };
        q.StartList(List("a", "b"), 0);
        Assert.Equal(Id("a"), q.Next(userInitiated: false)!.VideoId);
        Assert.Equal(Id("a"), q.PeekNext()!.VideoId);
        Assert.Equal(Id("b"), q.Next(userInitiated: true)!.VideoId);
    }

    [Fact]
    public void Shuffle_keeps_current_first_and_contains_same_items()
    {
        var q = new PlayQueue(new Random(42));
        q.StartList(List("a", "b", "c", "d", "e", "f"), 2);
        q.AppendRadio(List("r1", "r2"));
        q.Shuffle = true;
        Assert.Equal(0, q.CurrentIndex);
        Assert.Equal(Id("c"), q.Current!.VideoId);
        Assert.Equal(List("a", "b", "d", "e", "f").Select(t => t.VideoId).Order(), Ids(q).Skip(1).Take(5).Order());
        Assert.Equal([Id("r1"), Id("r2")], Ids(q).TakeLast(2)); // radio part untouched
        Assert.NotEqual([Id("c"), Id("a"), Id("b"), Id("d"), Id("e"), Id("f")], Ids(q).Take(6));
    }

    [Fact]
    public void Shuffle_on_before_start_shuffles_with_chosen_song_first()
    {
        var q = new PlayQueue(new Random(1)) { Shuffle = true };
        q.StartList(List("a", "b", "c", "d"), 3);
        Assert.Equal(Id("d"), q.Current!.VideoId);
        Assert.Equal(0, q.CurrentIndex);
        Assert.Equal(4, q.Items.Count);
    }

    [Fact]
    public void Shuffle_off_restores_original_order_after_current()
    {
        var q = new PlayQueue(new Random(7));
        q.StartList(List("a", "b", "c", "d", "e"), 0);
        q.Shuffle = true;
        q.Next(true);
        var current = q.Current!.VideoId;
        q.Shuffle = false;
        Assert.Equal(List("a", "b", "c", "d", "e").Select(t => t.VideoId), Ids(q));
        Assert.Equal(current, q.Current!.VideoId);
    }

    [Fact]
    public void PlayNext_inserts_after_current()
    {
        var q = new PlayQueue();
        q.StartList(List("a", "b", "c"), 0);
        q.PlayNext(T("n"));
        Assert.Equal([Id("a"), Id("n"), Id("b"), Id("c")], Ids(q));
        Assert.Equal(Id("n"), q.Next(false)!.VideoId);
    }

    [Fact]
    public void Move_and_Remove_adjust_CurrentIndex()
    {
        var q = new PlayQueue();
        q.StartList(List("a", "b", "c", "d"), 2); // current c @2
        q.Remove(0);                              // a gone → c @1
        Assert.Equal(1, q.CurrentIndex);
        Assert.Equal(Id("c"), q.Current!.VideoId);
        q.Move(2, 0);                             // d to front → [d,b,c]
        Assert.Equal(2, q.CurrentIndex);
        Assert.Equal(Id("c"), q.Current!.VideoId);
        q.Move(2, 0);                             // move current itself → [c,d,b]
        Assert.Equal(0, q.CurrentIndex);
        q.Remove(0);                              // removing current is ignored
        Assert.Equal(3, q.Items.Count);
    }

    [Fact]
    public void Empty_queue_is_safe()
    {
        var q = new PlayQueue();
        Assert.Null(q.Current);
        Assert.Null(q.Next(true));
        Assert.Null(q.Previous());
        Assert.False(q.NeedsRadio);
        q.Shuffle = true;
        q.Remove(3);
        q.Move(0, 1);
    }

    [Fact]
    public void ClearUpcoming_keeps_the_current_song_and_what_came_before()
    {
        var q = new PlayQueue();
        q.StartList(List("a", "b", "c", "d"), 1);
        q.ClearUpcoming();
        Assert.Equal([Id("a"), Id("b")], q.Items.Select(t => t.VideoId));
        Assert.Equal(Id("b"), q.Current!.VideoId);
        Assert.True(q.NeedsRadio);
        q.Shuffle = true;   // still consistent afterwards
        q.Shuffle = false;
        Assert.Equal(Id("b"), q.Current!.VideoId);
    }

    [Fact]
    public async Task Items_snapshot_survives_radio_appended_from_another_thread()
    {
        var q = new PlayQueue();
        q.StartList(List("a"), 0);
        var before = q.Items;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < 2000; i++) q.AppendRadio([T("r" + i)]);
        });
        while (!writer.IsCompleted)
            foreach (var t in q.Items) Assert.NotNull(t); // would throw "collection was modified" on a live list
        await writer;
        Assert.Single(before);
        Assert.Equal(2001, q.Items.Count);
    }
}
