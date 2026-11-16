using System.Globalization;
using Tune.Core.Library;
using Tune.Core.Util;

namespace Tune.Core.Sync;

public sealed record AccountProfile(string Uid, string? Email, string? Name, string? PhotoUrl);

/// <summary>
/// The signed-in Tune account (Google via Firebase) and the background sync that keeps likes, playlists and
/// history the same on every device: soon after a change here, when the app starts, and every few minutes.
/// </summary>
public sealed class AccountSync : IDisposable
{
    private readonly LibraryStore _lib;
    private readonly HttpClient _http;
    private readonly FirebaseAuthClient _auth;
    private readonly SemaphoreSlim _one = new(1, 1);
    private readonly Timer _timer;
    private readonly Timer _debounce;
    private AuthSession? _session;

    public static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AfterChange = TimeSpan.FromSeconds(4);

    public AccountSync(LibraryStore lib, HttpClient http, SyncConfig config)
    {
        _lib = lib;
        _http = http;
        Config = config;
        _auth = new FirebaseAuthClient(http, config);
        DeviceId = lib.GetSetting("sync.device") is { Length: > 0 } d ? d : NewDevice(lib);
        if (Get("account.uid") is { } uid && Get("account.refresh") is { } refresh)
            _session = new AuthSession(uid, Get("account.email"), Get("account.name"), Get("account.photo"), "", refresh, DateTimeOffset.MinValue);
        _timer = new Timer(_ => _ = SyncAsync(skipIfBusy: true), null, Timeout.Infinite, Timeout.Infinite);
        _debounce = new Timer(_ => _ = SyncAsync(skipIfBusy: true), null, Timeout.Infinite, Timeout.Infinite);
        lib.LocalChanged += OnLocalChanged;
    }

    public SyncConfig Config { get; }
    public string DeviceId { get; }
    public AccountProfile? Profile => _session is { } s ? new AccountProfile(s.Uid, s.Email, s.Name, s.PhotoUrl) : null;
    public bool IsSignedIn => _session is not null;
    public bool IsSyncing { get; private set; }
    public DateTimeOffset? LastSynced =>
        long.TryParse(Get("sync.last"), CultureInfo.InvariantCulture, out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    public string? LastError { get; private set; }

    /// <summary>Signed in/out, or a sync started/finished (status text may have changed). Any thread.</summary>
    public event Action? Changed;

    /// <summary>Begins the schedule (first sync right away when signed in).</summary>
    public void Start()
    {
        if (IsSignedIn) _timer.Change(TimeSpan.FromSeconds(3), Every);
    }

    public async Task SignInWithGoogleAsync(string googleIdToken, CancellationToken ct = default)
    {
        var s = await _auth.SignInWithGoogleAsync(googleIdToken, ct).ConfigureAwait(false);
        // a different account than last time: start over (this device's data is uploaded into the new account)
        if (Get("sync.lastUid") != s.Uid)
        {
            SyncEngine.ResetCursors(_lib);
            _lib.SetSetting("sync.lastUid", s.Uid);
        }
        _lib.MarkAllForSync();
        _session = s;
        Save(s);
        LastError = null;
        Changed?.Invoke();
        _timer.Change(TimeSpan.Zero, Every);
    }

    /// <summary>Stops syncing. Everything stays on this device.</summary>
    public void SignOut()
    {
        _session = null;
        foreach (var k in new[] { "account.uid", "account.email", "account.name", "account.photo", "account.refresh" }) _lib.SetSetting(k, "");
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        LastError = null;
        Changed?.Invoke();
    }

    /// <summary>
    /// One sync round now; if one is running, waits for it and then runs (so the latest changes go too).
    /// Null when signed out, skipped, or failed (see <see cref="LastError"/>). Never throws.
    /// </summary>
    public async Task<SyncResult?> SyncAsync(CancellationToken ct = default, bool skipIfBusy = false)
    {
        if (_session is null) return null;
        if (skipIfBusy) { if (!await _one.WaitAsync(0, ct).ConfigureAwait(false)) return null; }
        else await _one.WaitAsync(ct).ConfigureAwait(false);
        if (_session is not { } session) { _one.Release(); return null; }
        IsSyncing = true;
        Changed?.Invoke();
        try
        {
            var remote = new FirestoreRemote(_http, Config, session.Uid, TokenAsync);
            var result = await new SyncEngine(_lib, remote, DeviceId).SyncAsync(ct).ConfigureAwait(false);
            _lib.SetSetting("sync.last", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
            LastError = null;
            if (result.Pulled + result.Pushed > 0) Log.Info($"Sync: {result.Pulled} pulled, {result.Pushed} pushed");
            return result;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LastError = e is HttpRequestException or TaskCanceledException ? "No connection" : e.Message;
            Log.Warn($"Sync failed: {e.Message}");
            // the account was removed or the sign-in revoked: sign out instead of failing forever
            if (e is SyncException { Message: "TOKEN_EXPIRED" or "USER_NOT_FOUND" or "USER_DISABLED" or "INVALID_REFRESH_TOKEN" }) SignOut();
            return null;
        }
        finally
        {
            IsSyncing = false;
            _one.Release();
            Changed?.Invoke();
        }
    }

    private async Task<string> TokenAsync(bool force, CancellationToken ct)
    {
        var s = _session ?? throw new SyncException("Signed out");
        if (force || s.ExpiresAt - DateTimeOffset.UtcNow < TimeSpan.FromMinutes(2))
        {
            s = await _auth.RefreshAsync(s, ct).ConfigureAwait(false);
            if (_session is not null) { _session = s; Save(s); }
        }
        return s.IdToken;
    }

    private void OnLocalChanged()
    {
        if (IsSignedIn) _debounce.Change(AfterChange, Timeout.InfiniteTimeSpan);
    }

    private void Save(AuthSession s)
    {
        _lib.SetSetting("account.uid", s.Uid);
        _lib.SetSetting("account.email", s.Email ?? "");
        _lib.SetSetting("account.name", s.Name ?? "");
        _lib.SetSetting("account.photo", s.PhotoUrl ?? "");
        _lib.SetSetting("account.refresh", s.RefreshToken);
    }

    private string? Get(string key) => _lib.GetSetting(key) is { Length: > 0 } v ? v : null;

    private static string NewDevice(LibraryStore lib)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        lib.SetSetting("sync.device", id);
        return id;
    }

    public void Dispose()
    {
        _lib.LocalChanged -= OnLocalChanged;
        _timer.Dispose();
        _debounce.Dispose();
    }
}
