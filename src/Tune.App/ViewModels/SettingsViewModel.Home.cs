using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Feed;

namespace Tune.App.ViewModels;

/// <summary>A Home section in Settings > Home: shown or not, moved up or down.</summary>
public sealed partial class HomeSectionRow(HomeSection section, bool visible, Action changed) : ObservableObject
{
    [ObservableProperty] private bool _isVisible = visible;

    public HomeSection Section { get; } = section;
    public string Name => HomeLayout.Name(Section);
    public string Description => HomeLayout.Describe(Section);

    partial void OnIsVisibleChanged(bool value) => changed();
}

/// <summary>Settings categories (left rail) and the Home layout editor.</summary>
public sealed partial class SettingsViewModel
{
    /// <summary>appearance, home, playback, offline, library, window or about.</summary>
    [ObservableProperty] private string _category = "appearance";

    private ObservableCollection<HomeSectionRow>? _homeSections;

    public ObservableCollection<HomeSectionRow> HomeSections => _homeSections ??= LoadHomeSections();

    public string PinsStatus
    {
        get
        {
            var n = SpeedDialPins.Load(_shell.Services.Library).Count;
            return n == 0 ? "Nothing pinned. Use “Pin to speed dial” on a song, album or playlist." : $"{n} pinned";
        }
    }

    [RelayCommand]
    private void PickCategory(string category) => Category = category;

    private ObservableCollection<HomeSectionRow> LoadHomeSections() =>
        new(HomeLayout.Load(_shell.Services.Library).Select(s => new HomeSectionRow(s.Section, s.Visible, SaveHomeLayout)));

    [RelayCommand]
    private void MoveSectionUp(HomeSectionRow row) => Move(row, -1);

    [RelayCommand]
    private void MoveSectionDown(HomeSectionRow row) => Move(row, +1);

    private void Move(HomeSectionRow row, int by)
    {
        var i = HomeSections.IndexOf(row);
        var j = i + by;
        if (i < 0 || j < 0 || j >= HomeSections.Count) return;
        HomeSections.Move(i, j);
        SaveHomeLayout();
    }

    [RelayCommand]
    private void ResetHome()
    {
        HomeLayout.Save(_shell.Services.Library, HomeLayout.Default);
        _homeSections = null;
        OnPropertyChanged(nameof(HomeSections));
        _shell.HomeChanged();
    }

    [RelayCommand]
    private void ClearPins()
    {
        SpeedDialPins.Save(_shell.Services.Library, []);
        OnPropertyChanged(nameof(PinsStatus));
        _shell.HomeChanged();
    }

    private void SaveHomeLayout()
    {
        HomeLayout.Save(_shell.Services.Library, HomeSections.Select(r => new HomeSectionState(r.Section, r.IsVisible)));
        _shell.HomeChanged();
    }
}
