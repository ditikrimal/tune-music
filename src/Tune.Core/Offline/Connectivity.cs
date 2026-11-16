using System.Net.NetworkInformation;
using Tune.Core.Util;

namespace Tune.Core.Offline;

/// <summary>Whether YouTube can be reached right now.</summary>
public interface IConnectivity
{
    bool IsOnline { get; }

    /// <summary>Raised (on any thread) when <see cref="IsOnline"/> flips.</summary>
    event Action<bool>? Changed;

    /// <summary>Probes now (concurrent callers share one probe) and returns the fresh answer.</summary>
    Task<bool> CheckNowAsync();
}

/// <summary>
/// Watches Windows' network events and confirms with a tiny request. While online it never polls (no idle
/// cost); while offline it re-checks every <see cref="OfflinePollEvery"/> so Tune notices the way back.
/// </summary>
public sealed class ConnectivityMonitor : IConnectivity, IDisposable
{
    public static readonly TimeSpan OfflinePollEvery = TimeSpan.FromSeconds(20);

    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly Lock _gate = new();
    private Task<bool>? _running;
    private Timer? _poll;
    private volatile bool _online = true;

    public ConnectivityMonitor(Func<CancellationToken, Task<bool>> probe) => _probe = probe;

    public bool IsOnline => _online;
    public event Action<bool>? Changed;

    /// <summary>Subscribes to network changes and does a first check.</summary>
    public void Start()
    {
        NetworkChange.NetworkAvailabilityChanged += OnNetworkEvent;
        NetworkChange.NetworkAddressChanged += OnNetworkEvent;
        _ = CheckNowAsync();
    }

    private void OnNetworkEvent(object? sender, EventArgs e) =>
        // adapters settle over a second or two (DHCP, VPN reconnect): check now and once more shortly after
        _ = Task.Run(async () =>
        {
            await CheckNowAsync().ConfigureAwait(false);
            await Task.Delay(3000).ConfigureAwait(false);
            await CheckNowAsync().ConfigureAwait(false);
        });

    public Task<bool> CheckNowAsync()
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false } r) return r;
            return _running = ProbeAsync();
        }
    }

    private async Task<bool> ProbeAsync()
    {
        bool ok;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            ok = await _probe(cts.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            ok = false;
        }
        Set(ok);
        return ok;
    }

    /// <summary>Records a result (also used by tests).</summary>
    public void Set(bool online)
    {
        if (_online == online) return;
        _online = online;
        Log.Info(online ? "Back online" : "Offline");
        lock (_gate)
        {
            _poll?.Dispose();
            _poll = online ? null : new Timer(_ => _ = CheckNowAsync(), null, OfflinePollEvery, OfflinePollEvery);
        }
        Changed?.Invoke(online);
    }

    public void Dispose()
    {
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkEvent;
        NetworkChange.NetworkAddressChanged -= OnNetworkEvent;
        lock (_gate) _poll?.Dispose();
    }
}
