using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.App.Platform;
using Tune.Core.Util;

namespace Tune.App.ViewModels;

/// <summary>The Tune account (Continue with Google): likes, playlists and history synced between devices.</summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string _accountStatus = "";

    private bool _accountHooked;

    /// <summary>False when this build has no Firebase project baked in (then Settings hides the account row).</summary>
    public bool AccountAvailable => Services.Account is not null;
    public bool IsSignedIn => Services.Account?.IsSignedIn == true;
    public bool IsSignedOut => AccountAvailable && !IsSignedIn;
    public string AccountName => Services.Account?.Profile is { } p ? p.Name ?? p.Email ?? "Signed in" : "";
    public string? AccountEmail => Services.Account?.Profile?.Email;
    public string AccountGreeting => $"Hi, {AccountName.Split(' ')[0]}!";
    /// <summary>Profile card title: "Hi, Ditik!" or "Sign in to sync".</summary>
    public string AccountTitle => IsSignedIn ? AccountGreeting : AccountAvailable ? "Sign in to sync" : "Not signed in";
    /// <summary>Sidebar entry: your name, or "Profile" when signed out.</summary>
    public string AccountNavTitle => IsSignedIn ? AccountName.Split(' ')[0] : "Profile";
    public bool IsSyncing => Services.Account?.IsSyncing == true;
    public string AccountInitial => AccountName.Length > 0 ? AccountName[..1].ToUpperInvariant() : "?";
    public string? AccountPhoto => Services.Account?.Profile?.PhotoUrl is { Length: > 0 } u ? u : null;

    public void RefreshAccount()
    {
        if (Services.Account is not { } account)
        {
            AccountStatus = "Sign-in isn't available in this version.";
            return;
        }
        if (!_accountHooked)
        {
            _accountHooked = true;
            account.Changed += () => _ui.BeginInvoke(RefreshAccount);
        }
        AccountStatus = !account.IsSignedIn ? "Likes, playlists and history on all your devices."
            : account.IsSyncing ? "Syncing…"
            : account.LastError is { } err ? $"Couldn't sync: {err}"
            : account.LastSynced is { } at ? $"Synced {Ago(at)}"
            : "Waiting to sync";
        OnPropertyChanged(nameof(IsSignedIn));
        OnPropertyChanged(nameof(IsSignedOut));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(AccountGreeting));
        OnPropertyChanged(nameof(AccountTitle));
        OnPropertyChanged(nameof(AccountNavTitle));
        OnPropertyChanged(nameof(IsSyncing));
        OnPropertyChanged(nameof(AccountEmail));
        OnPropertyChanged(nameof(AccountInitial));
        OnPropertyChanged(nameof(AccountPhoto));
    }

    [RelayCommand]
    private async Task SignInAsync()
    {
        if (Services.Account is not { } account || IsSigningIn) return;
        IsSigningIn = true;
        AccountStatus = "Finish signing in in your browser…";
        try
        {
            var config = account.Config;
            string idToken;
            if (config.EmulatorHost is not null) idToken = EmulatorToken(); // testing against the local Firebase emulators
            else if (config.DesktopClientId is { } id && config.DesktopClientSecret is { } secret)
                idToken = await GoogleSignIn.GetIdTokenAsync(new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, id, secret, CancellationToken.None);
            else throw new InvalidOperationException("This build of Tune can't sign in (no Google client id).");
            await account.SignInWithGoogleAsync(idToken);
            ShowToast($"Signed in as {account.Profile?.Email ?? account.Profile?.Name}");
            System.Windows.Application.Current.MainWindow?.Activate();
        }
        catch (OperationCanceledException)
        {
            ShowToast("Sign-in cancelled");
        }
        catch (Exception e)
        {
            Log.Warn("Sign-in failed", e);
            ShowToast("Couldn't sign in: " + e.Message);
        }
        finally
        {
            IsSigningIn = false;
            RefreshAccount();
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        Services.Account?.SignOut();
        ShowToast("Signed out. Everything stays on this PC.");
        RefreshAccount();
    }

    [RelayCommand]
    private async Task SyncNowAsync()
    {
        if (Services.Account is not { } account) return;
        await Task.Run(() => account.SyncAsync());
        RefreshAccount();
    }

    /// <summary>Changes from another device were applied: reload what shows likes, playlists or history.</summary>
    public void OnSyncApplied()
    {
        ReloadPlaylists();
        LikesChanged();
        Player.Refresh();
        if (Page is CollectionViewModel c && (c.IsLocal || c.IsLiked || c.Kind == CollectionKind.History))
        {
            if (c.IsLocal && !Playlists.Any(p => p.Id == c.PlaylistId)) GoToCommand.Execute("home"); // deleted elsewhere
            else c.Reload();
        }
    }

    private static string Ago(DateTimeOffset at)
    {
        var d = DateTimeOffset.UtcNow - at;
        return d < TimeSpan.FromMinutes(1) ? "just now"
            : d < TimeSpan.FromHours(1) ? $"{(int)d.TotalMinutes} min ago"
            : d < TimeSpan.FromDays(1) ? $"{(int)d.TotalHours} h ago"
            : at.LocalDateTime.ToString("d MMM, HH:mm", System.Globalization.CultureInfo.CurrentCulture);
    }

    private static string EmulatorToken()
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var who = Environment.GetEnvironmentVariable("TUNE_EMULATOR_USER") ?? "tester";
        return $"{B64("{\"alg\":\"none\"}")}.{B64($"{{\"sub\":\"{who}\",\"email\":\"{who}@example.com\",\"email_verified\":true,\"name\":\"Test {who}\"}}")}.";
    }
}
