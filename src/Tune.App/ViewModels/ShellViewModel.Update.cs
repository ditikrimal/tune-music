using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.App.Platform;
using Tune.Core.Update;
using Tune.Core.Util;

namespace Tune.App.ViewModels;

/// <summary>New Tune releases: found on GitHub, shown in the sidebar and Settings, installed with one click.</summary>
public sealed partial class ShellViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateButtonText))]
    private ReleaseInfo? _availableUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private bool _isInstallingUpdate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    private double _updateProgress;

    [ObservableProperty] private string _updateStatus = "";

    private bool _checking;

    public bool HasUpdate => AvailableUpdate is not null;

    public string UpdateButtonText => IsInstallingUpdate
        ? $"Downloading {UpdateProgress:P0}"
        : AvailableUpdate is { } u ? $"Update to {u.Version.ToString(3)}" : "Check for updates";

    /// <summary>Looks for a newer release. Quiet unless something is found or the user pressed the button.</summary>
    public async Task CheckForUpdateAsync(bool userAsked)
    {
        if (_checking || IsInstallingUpdate) return;
        _checking = true;
        if (userAsked) UpdateStatus = "Checking…";
        try
        {
            var found = await Task.Run(() => Services.Updater.CheckAsync());
            var announce = found is not null && found.Tag != AvailableUpdate?.Tag;
            AvailableUpdate = found;
            UpdateStatus = found is not null
                ? $"Tune {found.Version.ToString(3)} is available."
                : $"You have the latest version ({AppUpdater.Current.ToString(3)}).";
            if (announce && !userAsked) ShowToast($"Tune {found!.Version.ToString(3)} is available — click Update at the top");
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Log.Warn("Update check failed", e);
            if (userAsked) UpdateStatus = "Couldn't check for updates — are you online?";
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>Sidebar/Settings button: check, or download the new version and restart into it.</summary>
    [RelayCommand]
    private async Task InstallUpdate()
    {
        if (IsInstallingUpdate) return;
        if (AvailableUpdate is not { } release)
        {
            await CheckForUpdateAsync(userAsked: true);
            return;
        }
        if (!AppUpdater.CanSelfUpdate || release.AssetUrl is null)
        {
            AppUpdater.OpenPage(release.PageUrl); // developer build: get it from the release page
            return;
        }
        IsInstallingUpdate = true;
        UpdateProgress = 0;
        UpdateStatus = $"Downloading Tune {release.Version.ToString(3)}…";
        try
        {
            var file = await Task.Run(() => Services.Updater.DownloadAsync(release, p => _ui.BeginInvoke(() => UpdateProgress = p)));
            UpdateStatus = "Restarting…";
            AppUpdater.ApplyAndRestart(file);
            App.Quit();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException
                                      or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Log.Warn("Update failed", e);
            UpdateStatus = "The update didn't finish — try again, or download it from the release page.";
            ShowToast("Update didn't finish — try again later");
        }
        finally
        {
            IsInstallingUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage() =>
        AppUpdater.OpenPage(AvailableUpdate?.PageUrl ?? "https://github.com/ditikrimal/tune-music/releases/latest");
}
