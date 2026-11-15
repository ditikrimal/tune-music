using Tune.Core.Streams;

namespace Tune.Core.Tests;

public class StreamPrefetcherTests
{
    private sealed class GatedResolver : IStreamResolver
    {
        public List<string> Started { get; } = [];
        public Dictionary<string, TaskCompletionSource> Gates { get; } = [];
        public HashSet<string> Known { get; } = [];
        public int Running;

        public Task PrepareAsync(string videoId, CancellationToken ct = default)
        {
            lock (Started) Started.Add(videoId);
            Interlocked.Increment(ref Running);
            var gate = Gates[videoId] = new TaskCompletionSource();
            return gate.Task.ContinueWith(_ => Interlocked.Decrement(ref Running), TaskScheduler.Default);
        }

        public void Finish(string id)
        {
            Known.Add(id);
            Gates[id].SetResult();
        }

        public bool IsKnown(string videoId) => Known.Contains(videoId) || Gates.ContainsKey(videoId);
        public bool IsReady(string videoId) => Known.Contains(videoId);
        public Task<Uri> ResolveAsync(string videoId, CancellationToken ct = default) => throw new NotSupportedException();
        public void Invalidate(string videoId) { }
        public bool ReportBroken(string videoId) => false;
    }

    private static async Task Settle() => await Task.Delay(50);

    [Fact]
    public async Task Runs_at_most_three_lookups_at_once()
    {
        var r = new GatedResolver();
        var p = new StreamPrefetcher(r, maxConcurrent: 3);
        p.Request(["a", "b", "c", "d", "e"], PrefetchPriority.Page);
        await Settle();
        Assert.Equal(["a", "b", "c"], r.Started);
        r.Finish("a");
        await Settle();
        Assert.Equal(["a", "b", "c", "d"], r.Started);
    }

    [Fact]
    public async Task Hover_jumps_ahead_of_page_items()
    {
        var r = new GatedResolver();
        var p = new StreamPrefetcher(r, maxConcurrent: 1);
        p.Request(["a", "b", "c"], PrefetchPriority.Page);
        await Settle();
        p.Request(["z"], PrefetchPriority.Hover);
        r.Finish("a");
        await Settle();
        Assert.Equal(["a", "z"], r.Started);
    }

    [Fact]
    public async Task New_page_replaces_old_page_leftovers()
    {
        var r = new GatedResolver();
        var p = new StreamPrefetcher(r, maxConcurrent: 1);
        p.Request(["a", "b", "c"], PrefetchPriority.Page);
        await Settle();
        p.Request(["x", "y"], PrefetchPriority.Page);   // user moved to another page
        r.Finish("a");
        await Settle();
        r.Finish("x");
        await Settle();
        Assert.Equal(["a", "x", "y"], r.Started);
    }

    [Fact]
    public async Task Skips_songs_that_are_already_known_or_queued()
    {
        var r = new GatedResolver();
        r.Known.Add("a");
        var p = new StreamPrefetcher(r, maxConcurrent: 3);
        p.Request(["a", "b", "b"], PrefetchPriority.Page);
        p.Request(["b"], PrefetchPriority.Hover);
        await Settle();
        Assert.Equal(["b"], r.Started);
    }

    [Fact]
    public async Task Failed_lookup_does_not_stall_the_queue()
    {
        var r = new GatedResolver();
        var p = new StreamPrefetcher(r, maxConcurrent: 1);
        p.Request(["a", "b"], PrefetchPriority.Page);
        await Settle();
        r.Gates["a"].SetException(new StreamResolveException("a", "unavailable"));
        await Settle();
        Assert.Equal(["a", "b"], r.Started);
    }
}
