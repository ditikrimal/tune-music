using System.Text;
using Tune.Core.Library;
using Tune.Core.Sync;
using static Tune.Core.Tests.LibraryStoreTests;

namespace Tune.Core.Tests;

/// <summary>
/// Against the real Firebase emulators (auth :9099 + Firestore :8080 with firestore.rules). Runs only when
/// TUNE_EMULATOR=127.0.0.1 is set: firebase emulators:start --only auth,firestore --project demo-tune
/// </summary>
public sealed class SyncEmulatorTests : IDisposable
{
    private static readonly string? Host = Environment.GetEnvironmentVariable("TUNE_EMULATOR");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tune-emu-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _http = new();
    private readonly SyncConfig _config = new("demo-tune", "fake-key", null, null, null, Host);

    public SyncEmulatorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _http.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    /// <summary>The auth emulator accepts unsigned Google ID tokens.</summary>
    private static string FakeGoogleToken(string sub, string email)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("{\"alg\":\"none\",\"kid\":\"x\"}")}.{B64($"{{\"sub\":\"{sub}\",\"email\":\"{email}\",\"email_verified\":true,\"name\":\"Test {sub}\",\"aud\":\"x\",\"iss\":\"https://accounts.google.com\"}}")}.";
    }

    [Fact]
    public async Task Two_devices_sync_through_firestore_and_users_are_kept_apart()
    {
        if (Host is null) return;
        var sub = Guid.NewGuid().ToString("N")[..10];

        using var pcLib = new LibraryStore(Path.Combine(_dir, "pc.db"));
        using var pc = new AccountSync(pcLib, _http, _config);
        await pc.SignInWithGoogleAsync(FakeGoogleToken(sub, $"{sub}@example.com"));
        Assert.Equal($"{sub}@example.com", pc.Profile!.Email);
        await pc.SyncAsync(); // the first sync starts by itself on sign-in; let it finish

        pcLib.SetLiked(T("a"), true);
        var p = pcLib.CreatePlaylist("Road trip");
        pcLib.AddManyToPlaylist(p, [T("b"), T("c")]);
        pcLib.RecordPlay(T("d"), DateTimeOffset.UtcNow);
        var first = await pc.SyncAsync();
        Assert.True(first is not null, pc.LastError);
        Assert.Null(pc.LastError);
        Assert.Equal(3, first!.Pushed); // the like, the playlist, today's plays

        // "reinstall": a new database signs in to the same Google account
        using var phoneLib = new LibraryStore(Path.Combine(_dir, "phone.db"));
        using var phone = new AccountSync(phoneLib, _http, _config);
        await phone.SignInWithGoogleAsync(FakeGoogleToken(sub, $"{sub}@example.com"));
        Assert.Equal(pc.Profile.Uid, phone.Profile!.Uid);
        await phone.SyncAsync();
        Assert.Null(phone.LastError);
        Assert.Equal([T("a").VideoId], phoneLib.GetLiked().Select(t => t.VideoId));
        Assert.Equal([T("b").VideoId, T("c").VideoId], phoneLib.GetPlaylistTracks(phoneLib.GetPlaylists().Single().Id).Select(t => t.VideoId));
        Assert.Equal([T("d").VideoId], phoneLib.GetRecent(5).Select(t => t.VideoId));

        // and back: unlike on the phone reaches the PC
        await Task.Delay(5);
        phoneLib.SetLiked(T("a"), false);
        await phone.SyncAsync();
        await pc.SyncAsync();
        Assert.Empty(pcLib.GetLiked());

        // a friend's account sees none of it, and can't read this user's documents
        using var friendLib = new LibraryStore(Path.Combine(_dir, "friend.db"));
        using var friend = new AccountSync(friendLib, _http, _config);
        await friend.SignInWithGoogleAsync(FakeGoogleToken(sub + "f", $"{sub}f@example.com"));
        await friend.SyncAsync();
        Assert.Empty(friendLib.GetPlaylists());
        var auth = new FirebaseAuthClient(_http, _config);
        var friendSession = await auth.SignInWithGoogleAsync(FakeGoogleToken(sub + "f", $"{sub}f@example.com"));
        var snoop = new FirestoreRemote(_http, _config, pc.Profile.Uid, (_, _) => Task.FromResult(friendSession.IdToken));
        var denied = await Assert.ThrowsAsync<SyncException>(() => snoop.PullAsync("likes", null, 10, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.Forbidden, denied.Status);
    }

    [Fact]
    public async Task Token_refresh_works()
    {
        if (Host is null) return;
        var auth = new FirebaseAuthClient(_http, _config);
        var s = await auth.SignInWithGoogleAsync(FakeGoogleToken("r" + Guid.NewGuid().ToString("N")[..8], "r@example.com"));
        var r = await auth.RefreshAsync(s);
        Assert.Equal(s.Uid, r.Uid);
        Assert.False(string.IsNullOrEmpty(r.IdToken));
    }
}
