using Tune.Core.Util;

namespace Tune.Core.Streams;

public enum PrefetchPriority
{
    /// <summary>The mouse is on it — the user is about to click.</summary>
    Hover = 0,

    /// <summary>Visible on the page the user is looking at.</summary>
    Page = 1,
}

/// <summary>
/// Prepares songs the user is likely to play next (visible on screen, under the mouse) so clicking them is
/// instant. A few lookups at a time; hovered songs first; a new page replaces the old page's leftovers.
/// </summary>
public sealed class StreamPrefetcher(IStreamResolver resolver, int maxConcurrent = 3)
{
    private readonly Lock _gate = new();
    private readonly LinkedList<string> _hover = new();
    private readonly LinkedList<string> _page = new();
    private int _running;

    public void Request(IEnumerable<string> videoIds, PrefetchPriority priority)
    {
        lock (_gate)
        {
            var list = priority == PrefetchPriority.Hover ? _hover : _page;
            if (priority == PrefetchPriority.Page) _page.Clear();
            foreach (var id in videoIds)
            {
                if (resolver.IsKnown(id) || _hover.Contains(id) || _page.Contains(id)) continue;
                if (priority == PrefetchPriority.Hover) list.AddFirst(id);
                else list.AddLast(id);
            }
        }
        Pump();
    }

    private void Pump()
    {
        while (true)
        {
            string id;
            lock (_gate)
            {
                if (_running >= maxConcurrent) return;
                var source = _hover.First is not null ? _hover : _page;
                if (source.First is null) return;
                id = source.First.Value;
                source.RemoveFirst();
                if (resolver.IsKnown(id)) continue;
                _running++;
            }
            _ = RunAsync(id);
        }
    }

    private async Task RunAsync(string id)
    {
        try
        {
            await resolver.PrepareAsync(id).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Log.Info($"Prefetch of {id} failed: {e.Message}");
        }
        finally
        {
            lock (_gate) _running--;
            Pump();
        }
    }
}
