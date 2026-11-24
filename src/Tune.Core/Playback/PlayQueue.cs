using Tune.Core.Models;

namespace Tune.Core.Playback;

public enum RepeatMode { Off, All, One }

/// <summary>
/// The play queue: a "list part" (the playlist/album/etc. the user started) followed by a "radio part"
/// (YouTube Music radio appended once the list runs out). Shuffle only reorders the list part.
/// </summary>
/// <remarks>
/// Thread-safe: radio is appended from a background task while the UI reads the queue. Every member takes
/// one lock, and <see cref="Items"/> hands out an immutable snapshot that later changes never touch.
/// </remarks>
public sealed class PlayQueue(Random? random = null)
{
    private readonly Random _random = random ?? Random.Shared;
    private readonly Lock _gate = new();
    private List<Track> _items = [];
    private List<Track> _original = []; // list part in its unshuffled order
    private readonly HashSet<string> _userAdded = []; // "play next" / moved by hand: never re-steered away
    private Track[]? _snapshot;
    private int _listCount;
    private int _currentIndex = -1;
    private bool _shuffle;
    private RepeatMode _repeat;

    public int CurrentIndex
    {
        get { lock (_gate) return _currentIndex; }
    }

    public Track? Current
    {
        get { lock (_gate) return CurrentUnlocked; }
    }

    private Track? CurrentUnlocked => _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;

    /// <summary>A snapshot of the queue; safe to enumerate while radio is being appended.</summary>
    public IReadOnlyList<Track> Items
    {
        get { lock (_gate) return _snapshot ??= [.. _items]; }
    }

    public RepeatMode Repeat
    {
        get { lock (_gate) return _repeat; }
        set { lock (_gate) _repeat = value; }
    }

    /// <summary>True when the queue is at its last song and more should be appended from radio.</summary>
    public bool NeedsRadio
    {
        get { lock (_gate) return _repeat != RepeatMode.All && _items.Count > 0 && _currentIndex >= _items.Count - 1; }
    }

    public string? RadioSeedId
    {
        get { lock (_gate) return _items.Count > 0 ? _items[^1].VideoId : null; }
    }

    public bool Shuffle
    {
        get { lock (_gate) return _shuffle; }
        set
        {
            lock (_gate)
            {
                if (_shuffle == value) return;
                _shuffle = value;
                if (_items.Count == 0) return;
                if (value) ApplyShuffle();
                else Unshuffle();
                _snapshot = null;
            }
        }
    }

    public void StartList(IReadOnlyList<Track> list, int startIndex)
    {
        lock (_gate)
        {
            _items = [.. list];
            _original = [.. list];
            _userAdded.Clear();
            _listCount = _items.Count;
            _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
            if (_shuffle && _items.Count > 0) ApplyShuffle();
            _snapshot = null;
        }
    }

    public void StartRadio(Track seed) => StartList([seed], 0);

    public Track? Next(bool userInitiated)
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            if (_repeat == RepeatMode.One && !userInitiated) return CurrentUnlocked;
            if (_currentIndex + 1 < _items.Count) _currentIndex++;
            else if (_repeat == RepeatMode.All) _currentIndex = 0;
            else return null;
            return CurrentUnlocked;
        }
    }

    public Track? PeekNext()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            if (_repeat == RepeatMode.One) return CurrentUnlocked;
            if (_currentIndex + 1 < _items.Count) return _items[_currentIndex + 1];
            return _repeat == RepeatMode.All ? _items[0] : null;
        }
    }

    public Track? Previous()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            if (_currentIndex > 0) _currentIndex--;
            return CurrentUnlocked;
        }
    }

    public void AppendRadio(IEnumerable<Track> tracks)
    {
        lock (_gate)
        {
            var present = _items.Select(t => t.VideoId).ToHashSet();
            foreach (var t in tracks)
                if (present.Add(t.VideoId)) _items.Add(t);
            _snapshot = null;
        }
    }

    /// <summary>Radio songs coming up after the next <paramref name="keep"/> (what <see cref="ReplaceAuto"/> may change).</summary>
    public int AutoUpcoming(int keep = 1)
    {
        lock (_gate)
        {
            if (_repeat == RepeatMode.All || _currentIndex < 0) return 0;
            var start = Math.Max(_listCount, _currentIndex + 1 + keep);
            return _items.Skip(start).Count(t => !_userAdded.Contains(t.VideoId));
        }
    }

    /// <summary>
    /// Re-steers the radio: every radio song after the next <paramref name="keep"/> is replaced by
    /// <paramref name="tracks"/>. The list part and songs the user queued or moved stay where they are; with
    /// <paramref name="includeList"/> (a tuner chip was picked) the rest of the list makes way too, like YouTube Music.
    /// </summary>
    public void ReplaceAuto(IReadOnlyList<Track> tracks, int keep = 1, bool includeList = false)
    {
        lock (_gate)
        {
            if (_repeat == RepeatMode.All || _currentIndex < 0) return;
            var start = Math.Min(_items.Count, includeList ? _currentIndex + 1 + keep : Math.Max(_listCount, _currentIndex + 1 + keep));
            var kept = _items.Take(start).Concat(_items.Skip(start).Where(t => _userAdded.Contains(t.VideoId))).ToList();
            var present = kept.Select(t => t.VideoId).ToHashSet();
            foreach (var t in tracks)
                if (present.Add(t.VideoId)) kept.Add(t);
            if (includeList && _listCount > start)
            {
                _listCount = start;
                _original = _shuffle ? _original.Where(t => kept.Take(start).Any(k => k.VideoId == t.VideoId)).ToList() : kept.Take(start).ToList();
            }
            _items = kept;
            _snapshot = null;
        }
    }

    public void PlayNext(Track t)
    {
        lock (_gate)
        {
            _userAdded.Add(t.VideoId);
            var at = Math.Max(_currentIndex + 1, 0);
            _items.Insert(at, t);
            if (at <= _listCount) _listCount++;
            if (_currentIndex < 0) _currentIndex = 0;
            if (_shuffle)
            {
                var origAt = CurrentUnlocked is { } cur ? _original.FindIndex(x => x.VideoId == cur.VideoId) + 1 : 0;
                _original.Insert(Math.Clamp(origAt, 0, _original.Count), t);
            }
            else SyncOriginal();
            _snapshot = null;
        }
    }

    public void Remove(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= _items.Count || index == _currentIndex) return;
            var removed = _items[index];
            _items.RemoveAt(index);
            if (index < _currentIndex) _currentIndex--;
            if (index < _listCount)
            {
                _listCount--;
                if (_shuffle) _original.RemoveAt(Math.Max(_original.FindIndex(x => x.VideoId == removed.VideoId), 0));
                else SyncOriginal();
            }
            _snapshot = null;
        }
    }

    public void Move(int from, int to)
    {
        lock (_gate)
        {
            if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to) return;
            var item = _items[from];
            _userAdded.Add(item.VideoId); // placed by hand: re-steering leaves it alone
            _items.RemoveAt(from);
            _items.Insert(to, item);

            if (from == _currentIndex) _currentIndex = to;
            else if (from < _currentIndex && to >= _currentIndex) _currentIndex--;
            else if (from > _currentIndex && to <= _currentIndex) _currentIndex++;

            var fromList = from < _listCount;
            var toList = to < _listCount || (to == _listCount && !fromList);
            if (fromList && !toList) _listCount--;
            else if (!fromList && toList) _listCount++;
            if (!_shuffle) SyncOriginal();
            _snapshot = null;
        }
    }

    /// <summary>Drops every song after the current one (radio may refill it later).</summary>
    public void ClearUpcoming()
    {
        lock (_gate)
        {
            if (_currentIndex < 0 || _currentIndex >= _items.Count - 1) return;
            _items.RemoveRange(_currentIndex + 1, _items.Count - _currentIndex - 1);
            _listCount = Math.Min(_listCount, _items.Count);
            _original = _items.Take(_listCount).ToList();
            _snapshot = null;
        }
    }

    // ---- helpers: callers hold _gate ----

    private void SyncOriginal() => _original = _items.Take(_listCount).ToList();

    private void ApplyShuffle()
    {
        if (_currentIndex < 0 || _currentIndex >= _listCount) return;
        var current = _items[_currentIndex];
        var others = _items.Take(_listCount).Where((_, i) => i != _currentIndex).ToArray();
        _random.Shuffle(others);
        _items = [current, .. others, .. _items.Skip(_listCount)];
        _currentIndex = 0;
    }

    private void Unshuffle()
    {
        if (_currentIndex < 0 || _currentIndex >= _listCount) return;
        var current = _items[_currentIndex];
        _items = [.. _original, .. _items.Skip(_listCount)];
        _listCount = _original.Count;
        _currentIndex = Math.Max(_original.FindIndex(t => t.VideoId == current.VideoId), 0);
    }
}
