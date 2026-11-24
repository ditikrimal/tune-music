using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Playback;

namespace Tune.App.ViewModels;

/// <summary>The "Queue" panel: now playing + what comes next; reorder by dragging, remove, play next, clear.</summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private const int MaxShown = 150;
    private readonly ShellViewModel _shell;
    private readonly PlaybackService _playback;

    [ObservableProperty] private TrackRowViewModel? _nowPlaying;
    [ObservableProperty] private string _summary = "";

    public QueueViewModel(ShellViewModel shell, Dispatcher ui)
    {
        _shell = shell;
        _playback = shell.Services.Playback;
        _playback.StateChanged += () => ui.BeginInvoke(() => { if (shell.IsQueueOpen || Watching) Refresh(); });
    }

    /// <summary>Something besides the queue panel shows the queue (the wide mini-player): keep it current.</summary>
    public bool Watching { get; set; }

    public ObservableCollection<TrackRowViewModel> Upcoming { get; } = [];
    public bool HasUpcoming => Upcoming.Count > 0;

    /// <summary>YouTube Music's radio tuner chips for the song playing: "All" (follow the vibe), then its own.</summary>
    public ObservableCollection<VibeChipViewModel> Chips { get; } = [];
    public bool HasChips => Chips.Count > 1;

    /// <summary>"Following your downbeat vibe" / "Tuned to Popular".</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasVibeNote))] private string _vibeNote = "";
    public bool HasVibeNote => VibeNote.Length > 0;

    private string _chipKey = "";

    private void RefreshChips()
    {
        var chips = _playback.CurrentChips;
        var tuning = _playback.Tuning;
        var key = string.Join('|', chips.Select(c => c.Name)) + "#" + tuning;
        if (key != _chipKey)
        {
            _chipKey = key;
            Chips.Clear();
            if (chips.Count > 0)
            {
                Chips.Add(new VibeChipViewModel("All", null, tuning is null));
                // a chip picked earlier stays visible even if this song doesn't have it
                if (tuning is not null && chips.All(c => c.Name != tuning)) Chips.Add(new VibeChipViewModel(tuning, tuning, true));
                foreach (var c in chips) Chips.Add(new VibeChipViewModel(c.Name, c.Name, c.Name == tuning));
            }
            OnPropertyChanged(nameof(HasChips));
        }
        VibeNote = tuning is { } t ? $"Tuned to {t}"
            : _playback.CurrentVibe is { } v ? $"Following your {v.ToLowerInvariant()} vibe"
            : HasChips ? "Following what you play" : "";
    }

    /// <summary>A chip was clicked: tune the radio to it ("All" = follow the vibe again).</summary>
    [RelayCommand]
    private void Tune(string? chip)
    {
        if (chip == _playback.Tuning) return;
        _playback.Tune(chip);
        Refresh();
    }

    public void Refresh()
    {
        RefreshChips();
        var q = _playback.Queue;
        var items = q.Items;
        var cur = q.CurrentIndex;
        NowPlaying = cur >= 0 && cur < items.Count ? Row(items[cur], cur, current: true) : null;
        Upcoming.Clear();
        for (var i = cur + 1; i < items.Count && Upcoming.Count < MaxShown; i++)
            Upcoming.Add(Row(items[i], i, current: false));

        var rest = items.Skip(cur + 1).ToList();
        var total = TimeSpan.FromSeconds(rest.Sum(t => t.Duration?.TotalSeconds ?? 0));
        Summary = rest.Count == 0 ? "" : ShellViewModel.Songs(rest.Count) + (total > TimeSpan.Zero ? " · " + Long(total) : "");
        OnPropertyChanged(nameof(HasUpcoming));
    }

    private TrackRowViewModel Row(Tune.Core.Models.Track t, int index, bool current)
    {
        var row = new TrackRowViewModel(t, index) { IsCurrent = current, IsLiked = _shell.Services.Library.IsLiked(t.VideoId) };
        row.SyncDownload(_shell.Services.Downloads, _shell.IsOffline);
        return row;
    }

    private static string Long(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours} hr {t.Minutes} min" : $"{Math.Max(1, (int)Math.Round(t.TotalMinutes))} min";

    /// <summary>Play a song from the queue: jump to it (keeps the rest of the queue).</summary>
    [RelayCommand]
    private async Task Jump(TrackRowViewModel? row)
    {
        if (row is null || row.IsCurrent) return;
        var q = _playback.Queue;
        q.Move(row.Number, q.CurrentIndex + 1);
        await _playback.NextAsync();
        Refresh();
    }

    /// <summary>Moves the song right after the one playing.</summary>
    public void PlayNext(TrackRowViewModel row)
    {
        var q = _playback.Queue;
        if (row.IsCurrent || row.Number == q.CurrentIndex + 1) return;
        q.Move(row.Number, q.CurrentIndex + 1);
        _playback.NotifyQueueChanged();
        Refresh();
    }

    [RelayCommand]
    private void Remove(TrackRowViewModel? row)
    {
        if (row is null || row.IsCurrent) return;
        _playback.Queue.Remove(row.Number);
        _playback.NotifyQueueChanged();
        Refresh();
    }

    /// <summary>Drag and drop: <paramref name="row"/> takes <paramref name="target"/>'s place.</summary>
    public void Move(TrackRowViewModel row, TrackRowViewModel target)
    {
        if (row.IsCurrent || target.IsCurrent || row == target) return;
        _playback.Queue.Move(row.Number, target.Number);
        _playback.NotifyQueueChanged();
        Refresh();
    }

    [RelayCommand]
    private void Clear()
    {
        if (Upcoming.Count == 0) return;
        _playback.Queue.ClearUpcoming();
        _playback.NotifyQueueChanged();
        Refresh();
        _shell.ShowToast("Queue cleared");
    }

    [RelayCommand]
    private void Close() => _shell.IsQueueOpen = false;
}

/// <summary>One tuner chip: <see cref="Key"/> is what it tunes to (null = "All").</summary>
public sealed record VibeChipViewModel(string Name, string? Key, bool IsSelected)
{
    public string State => IsSelected ? "On" : "";
}
