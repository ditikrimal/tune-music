using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Tune.Core.Playback;
using Tune.Core.Util;

namespace Tune.App.Audio;

/// <summary>Audio-only player on libmpv. Events are raised on mpv's event thread.</summary>
public sealed class MpvPlayer : IAudioPlayer, IDisposable
{
    private const ulong TimePosId = 1, DurationId = 2, PauseId = 3;
    private static readonly long PositionIntervalTicks = Stopwatch.Frequency / 4; // ~4 Hz

    private readonly nint _ctx;
    private readonly Thread _thread;
    private volatile bool _disposed;
    private double _position;
    private double? _duration;
    private bool _paused;
    private double _volume = 1;
    private long _lastPositionTick;

    public MpvPlayer()
    {
        _ctx = MpvNative.Create();
        if (_ctx == 0) throw new InvalidOperationException("mpv_create failed");
        foreach (var (k, v) in new[]
                 {
                     ("vid", "no"), ("video", "no"), ("ytdl", "no"), ("audio-display", "no"), ("terminal", "no"),
                     ("idle", "yes"), ("cache", "yes"), ("demuxer-max-bytes", "8MiB"), ("demuxer-max-back-bytes", "2MiB"),
                     ("volume-max", "100"), ("audio-client-name", "Tune"), ("input-default-bindings", "no"),
                     ("network-timeout", "15"),
                 })
            MpvNative.SetOptionString(_ctx, k, v);
        var err = MpvNative.Initialize(_ctx);
        if (err < 0) throw new InvalidOperationException("mpv_initialize failed: " + MpvNative.Error(err));

        MpvNative.ObserveProperty(_ctx, TimePosId, "time-pos", MpvNative.FormatDouble);
        MpvNative.ObserveProperty(_ctx, DurationId, "duration", MpvNative.FormatDouble);
        MpvNative.ObserveProperty(_ctx, PauseId, "pause", MpvNative.FormatFlag);

        _thread = new Thread(EventLoop) { IsBackground = true, Name = "mpv-events" };
        _thread.Start();
    }

    public event Action? Ended;
    public event Action<string>? Failed;
    public event Action? PositionChanged;

    public TimeSpan Position => TimeSpan.FromSeconds(Volatile.Read(ref _position));
    public TimeSpan? Duration => _duration is { } d ? TimeSpan.FromSeconds(d) : null;
    public bool IsPaused => _paused;

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            MpvNative.SetPropertyString(_ctx, "volume", (_volume * 100).ToString("0.#", CultureInfo.InvariantCulture));
        }
    }

    public Task LoadAsync(Uri url, TimeSpan startAt)
    {
        var target = url.IsFile ? url.LocalPath : url.AbsoluteUri;
        _duration = null;
        Volatile.Write(ref _position, startAt.TotalSeconds);
        var start = "start=" + startAt.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        var err = MpvNative.Command(_ctx, "loadfile", target, "replace", "-1", start);
        if (err < 0) Failed?.Invoke(MpvNative.Error(err));
        return Task.CompletedTask;
    }

    public void Play()
    {
        _paused = false;
        MpvNative.SetPropertyString(_ctx, "pause", "no");
    }

    public void Pause()
    {
        _paused = true;
        MpvNative.SetPropertyString(_ctx, "pause", "yes");
    }

    public void Stop() => MpvNative.Command(_ctx, "stop");

    public void Seek(TimeSpan pos)
    {
        Volatile.Write(ref _position, pos.TotalSeconds);
        MpvNative.Command(_ctx, "seek", pos.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "absolute");
    }

    private void EventLoop()
    {
        while (!_disposed)
        {
            var ptr = MpvNative.WaitEvent(_ctx, 1.0);
            if (ptr == 0 || _disposed) continue;
            var ev = Marshal.PtrToStructure<MpvNative.Event>(ptr);
            try
            {
                switch (ev.EventId)
                {
                    case MpvNative.EventShutdown:
                        return;
                    case MpvNative.EventPropertyChange:
                        OnProperty(ev.ReplyUserdata, Marshal.PtrToStructure<MpvNative.EventProperty>(ev.Data));
                        break;
                    case MpvNative.EventEndFile:
                        var end = Marshal.PtrToStructure<MpvNative.EndFileData>(ev.Data);
                        if (end.Reason == MpvNative.EndReasonEof) Ended?.Invoke();
                        else if (end.Reason == MpvNative.EndReasonError) Failed?.Invoke(MpvNative.Error(end.Error));
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error("mpv event handler failed", e); // a listener bug must not kill the event thread
            }
        }
    }

    private void OnProperty(ulong id, MpvNative.EventProperty prop)
    {
        if (prop.Data == 0) return; // property unavailable (e.g. nothing loaded)
        switch (id)
        {
            case TimePosId when prop.Format == MpvNative.FormatDouble:
                Volatile.Write(ref _position, Marshal.PtrToStructure<double>(prop.Data));
                var now = Stopwatch.GetTimestamp();
                if (now - _lastPositionTick >= PositionIntervalTicks)
                {
                    _lastPositionTick = now;
                    PositionChanged?.Invoke();
                }
                break;
            case DurationId when prop.Format == MpvNative.FormatDouble:
                _duration = Marshal.PtrToStructure<double>(prop.Data);
                break;
            case PauseId when prop.Format == MpvNative.FormatFlag:
                _paused = Marshal.ReadInt32(prop.Data) != 0;
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        MpvNative.Wakeup(_ctx);
        _thread.Join(TimeSpan.FromSeconds(2));
        MpvNative.TerminateDestroy(_ctx);
    }
}
