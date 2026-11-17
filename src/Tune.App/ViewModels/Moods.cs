using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Tune.Core.Models;
using Tune.Core.Util;

namespace Tune.App.ViewModels;

/// <summary>A Moods &amp; genres tile on the Search page.</summary>
public sealed partial class MoodTileViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;

    public MoodTileViewModel(ShellViewModel shell, MoodCategory mood)
    {
        _shell = shell;
        Mood = mood;
        _cover = mood.ThumbnailUrl;
        var c = Color.FromArgb(0xFF, (byte)(mood.Color >> 16), (byte)(mood.Color >> 8), (byte)mood.Color);
        Stripe = Frozen(new SolidColorBrush(c));
        Tint = Frozen(new LinearGradientBrush(Blend(c, Color.FromRgb(0x1C, 0x1C, 0x1E), 0.30), Blend(c, Color.FromRgb(0x17, 0x17, 0x19), 0.08), 20));
    }

    public MoodCategory Mood { get; }
    public string Title => Mood.Title;
    /// <summary>The small bar under the title, in YouTube Music's colour for this tile.</summary>
    public Brush Stripe { get; }
    /// <summary>The tile's background: dark, washed with its colour.</summary>
    public Brush Tint { get; }
    [ObservableProperty] private string? _cover;

    [RelayCommand]
    private void Open() => _shell.Navigate(new MoodViewModel(_shell, Mood, Stripe));

    private static Color Blend(Color c, Color over, double amount) => Color.FromRgb(
        (byte)(over.R + (c.R - over.R) * amount), (byte)(over.G + (c.G - over.G) * amount), (byte)(over.B + (c.B - over.B) * amount));

    private static T Frozen<T>(T b) where T : System.Windows.Freezable { b.Freeze(); return b; }
}

/// <summary>One mood or genre: YouTube Music's rows of playlists for it.</summary>
public sealed partial class MoodViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;

    public MoodViewModel(ShellViewModel shell, MoodCategory mood, Brush accent)
    {
        _shell = shell;
        Title = mood.Title;
        Accent = accent;
        _ = LoadAsync(mood);
    }

    public string? NavKey => "search";
    public string Title { get; }
    public Brush Accent { get; }
    [ObservableProperty] private IReadOnlyList<Shelf> _shelves = [];
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _message;

    private async Task LoadAsync(MoodCategory mood)
    {
        try
        {
            Shelves = await _shell.Services.Api.GetMoodAsync(mood.Params);
            if (Shelves.Count == 0) Message = "Nothing here right now.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn($"Mood {mood.Title} failed", e);
            Message = "Couldn't load this — check your connection.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}

public sealed partial class SearchViewModel
{
    /// <summary>"Moods &amp; moments" and "Genres", shown while the search box is empty.</summary>
    public ObservableCollection<MoodSectionViewModel> Browse { get; } = [];

    public bool ShowBrowse => Query.Trim().Length == 0 && Browse.Count > 0;

    private async Task LoadBrowseAsync()
    {
        var sections = await _shell.Services.Moods.GetAsync();
        foreach (var s in sections)
            Browse.Add(new MoodSectionViewModel(s.Title, s.Items.Select(i => new MoodTileViewModel(_shell, i)).ToList()));
        OnPropertyChanged(nameof(ShowBrowse));
        // pictures for tiles that don't have one yet (once; they're remembered for a week)
        foreach (var tile in Browse.SelectMany(s => s.Tiles).Where(t => t.Cover is null).ToList())
            _ = FillCoverAsync(tile);
    }

    private async Task FillCoverAsync(MoodTileViewModel tile) => tile.Cover = await _shell.Services.Moods.CoverAsync(tile.Mood);
}

public sealed record MoodSectionViewModel(string Title, IReadOnlyList<MoodTileViewModel> Tiles);
