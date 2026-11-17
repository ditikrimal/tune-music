using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Tune.Core.Models;
using Tune.Core.Util;

namespace Tune.App.ViewModels;

/// <summary>"Show all" from an artist's Albums / Singles &amp; EPs row: every release as a grid of cards.</summary>
public sealed partial class GridViewModel : ObservableObject, IPage
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private string? _subtitle;
    [ObservableProperty] private ObservableCollection<Card> _items = [];
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private string? _message;

    public GridViewModel(ShellViewModel shell, string title, string? subtitle, BrowseLink link)
    {
        _title = title;
        _subtitle = subtitle;
        _ = LoadAsync(shell, link);
    }

    public string? NavKey => null;

    private async Task LoadAsync(ShellViewModel shell, BrowseLink link)
    {
        try
        {
            var page = await shell.Services.Api.GetGridAsync(link);
            if (Subtitle is null && page.Title.Length > 0) Subtitle = page.Title;
            Items = new ObservableCollection<Card>(page.Items);
            if (page.Items.Count == 0) Message = "Nothing more to show here.";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Warn("Grid load failed", e);
            Message = "Couldn't load this — check your connection.";
        }
        finally
        {
            IsLoading = false;
        }
    }
}
