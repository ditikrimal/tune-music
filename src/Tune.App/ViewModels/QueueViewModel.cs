using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Playback;

namespace Tune.App.ViewModels;

/// <summary>The right-hand "Queue" panel: now playing + what comes next.</summary>
public sealed partial class QueueViewModel : ObservableObject
{
    private const int MaxShown = 100;
    private readonly ShellViewModel _shell;
    private readonly PlaybackService _playback;

    [ObservableProperty] private TrackRowViewModel? _nowPlaying;

    public QueueViewModel(ShellViewModel shell, Dispatcher ui)
    {
        _shell = shell;
        _playback = shell.Services.Playback;
        _playback.StateChanged += () => ui.BeginInvoke(() => { if (shell.IsQueueOpen) Refresh(); });
    }

    public ObservableCollection<TrackRowViewModel> Upcoming { get; } = [];

    public void Refresh()
    {
        var q = _playback.Queue;
        NowPlaying = q.Current is { } cur ? new TrackRowViewModel(cur, 0) { IsCurrent = true } : null;
        Upcoming.Clear();
        for (var i = q.CurrentIndex + 1; i < q.Items.Count && Upcoming.Count < MaxShown; i++)
            Upcoming.Add(new TrackRowViewModel(q.Items[i], i));
    }

    /// <summary>Play a song from the queue: jump to it (keeps the rest of the queue).</summary>
    [RelayCommand]
    private async Task Jump(TrackRowViewModel? row)
    {
        if (row is null) return;
        var q = _playback.Queue;
        // move the chosen item right after the current one, then skip to it
        q.Move(row.Number, q.CurrentIndex + 1);
        await _playback.NextAsync();
        Refresh();
    }

    [RelayCommand]
    private void Remove(TrackRowViewModel? row)
    {
        if (row is null) return;
        _playback.Queue.Remove(row.Number);
        _playback.NotifyQueueChanged();
        Refresh();
    }

    public void Move(TrackRowViewModel row, TrackRowViewModel target)
    {
        _playback.Queue.Move(row.Number, target.Number);
        _playback.NotifyQueueChanged();
        Refresh();
    }

    [RelayCommand]
    private void Close() => _shell.IsQueueOpen = false;
}
