using Tune.Core.Models;

namespace Tune.Core.Playback;

public enum RepeatMode { Off, All, One }

/// <summary>
/// The play queue: a "list part" (the playlist/album/etc. the user started) followed by a "radio part"
/// (YouTube Music radio appended once the list runs out). Shuffle only reorders the list part.
/// </summary>
public sealed class PlayQueue(Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;
    private List<Track> _items = [];
    private List<Track> _original = []; // list part in its unshuffled order
    private int _listCount;
    private bool _shuffle;

    public int CurrentIndex { get; private set; } = -1;
    public Track? Current => CurrentIndex >= 0 && CurrentIndex < _items.Count ? _items[CurrentIndex] : null;
    public IReadOnlyList<Track> Items => _items;
    public RepeatMode Repeat { get; set; }

    /// <summary>True when the queue is at its last song and more should be appended from radio.</summary>
    public bool NeedsRadio => Repeat != RepeatMode.All && _items.Count > 0 && CurrentIndex >= _items.Count - 1;

    public string? RadioSeedId => _items.Count > 0 ? _items[^1].VideoId : null;

    public bool Shuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            if (_items.Count == 0) return;
            if (value) ApplyShuffle();
            else Unshuffle();
        }
    }

    public void StartList(IReadOnlyList<Track> list, int startIndex)
    {
        _items = [.. list];
        _original = [.. list];
        _listCount = _items.Count;
        CurrentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
        if (_shuffle && _items.Count > 0) ApplyShuffle();
    }

    public void StartRadio(Track seed) => StartList([seed], 0);

    public Track? Next(bool userInitiated)
    {
        if (_items.Count == 0) return null;
        if (Repeat == RepeatMode.One && !userInitiated) return Current;
        if (CurrentIndex + 1 < _items.Count) CurrentIndex++;
        else if (Repeat == RepeatMode.All) CurrentIndex = 0;
        else return null;
        return Current;
    }

    public Track? PeekNext()
    {
        if (_items.Count == 0) return null;
        if (Repeat == RepeatMode.One) return Current;
        if (CurrentIndex + 1 < _items.Count) return _items[CurrentIndex + 1];
        return Repeat == RepeatMode.All ? _items[0] : null;
    }

    public Track? Previous()
    {
        if (_items.Count == 0) return null;
        if (CurrentIndex > 0) CurrentIndex--;
        return Current;
    }

    public void AppendRadio(IEnumerable<Track> tracks)
    {
        var present = _items.Select(t => t.VideoId).ToHashSet();
        foreach (var t in tracks)
            if (present.Add(t.VideoId)) _items.Add(t);
    }

    public void PlayNext(Track t)
    {
        var at = Math.Max(CurrentIndex + 1, 0);
        _items.Insert(at, t);
        if (at <= _listCount) _listCount++;
        if (CurrentIndex < 0) CurrentIndex = 0;
        if (_shuffle)
        {
            var origAt = Current is { } cur ? _original.FindIndex(x => x.VideoId == cur.VideoId) + 1 : 0;
            _original.Insert(Math.Clamp(origAt, 0, _original.Count), t);
        }
        else SyncOriginal();
    }

    public void Remove(int index)
    {
        if (index < 0 || index >= _items.Count || index == CurrentIndex) return;
        var removed = _items[index];
        _items.RemoveAt(index);
        if (index < CurrentIndex) CurrentIndex--;
        if (index < _listCount)
        {
            _listCount--;
            if (_shuffle) _original.RemoveAt(Math.Max(_original.FindIndex(x => x.VideoId == removed.VideoId), 0));
            else SyncOriginal();
        }
    }

    public void Move(int from, int to)
    {
        if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return;
        var item = _items[from];
        _items.RemoveAt(from);
        _items.Insert(to, item);

        if (from == CurrentIndex) CurrentIndex = to;
        else if (from < CurrentIndex && to >= CurrentIndex) CurrentIndex--;
        else if (from > CurrentIndex && to <= CurrentIndex) CurrentIndex++;

        var fromList = from < _listCount;
        var toList = to < _listCount || (to == _listCount && !fromList);
        if (fromList && !toList) _listCount--;
        else if (!fromList && toList) _listCount++;
        if (!_shuffle) SyncOriginal();
    }

    private void SyncOriginal() => _original = _items.Take(_listCount).ToList();

    private void ApplyShuffle()
    {
        if (CurrentIndex < 0 || CurrentIndex >= _listCount) return;
        var current = _items[CurrentIndex];
        var others = _items.Take(_listCount).Where((_, i) => i != CurrentIndex).ToArray();
        _random.Shuffle(others);
        _items = [current, .. others, .. _items.Skip(_listCount)];
        CurrentIndex = 0;
    }

    private void Unshuffle()
    {
        if (CurrentIndex < 0 || CurrentIndex >= _listCount) return;
        var current = _items[CurrentIndex];
        _items = [.. _original, .. _items.Skip(_listCount)];
        _listCount = _original.Count;
        CurrentIndex = Math.Max(_original.FindIndex(t => t.VideoId == current.VideoId), 0);
    }
}
