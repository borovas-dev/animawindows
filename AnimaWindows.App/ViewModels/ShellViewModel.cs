using System.Collections.ObjectModel;
using AnimaWindows.App.Pages.Search;
using AnimaWindows.App.Services;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Updates;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimaWindows.App.ViewModels;

/// <summary>State of the title-bar search box and the update banner.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    private readonly SearchViewModel _search;
    private readonly INavigationService _navigation;
    private readonly IDispatcher _dispatcher;
    private readonly UpdateChecker _updates;

    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; set; }

    [ObservableProperty]
    public partial string UpdateMessage { get; set; }

    [ObservableProperty]
    public partial Uri? UpdatePageUri { get; set; }

    public ShellViewModel(SearchViewModel search, INavigationService navigation, IDispatcher dispatcher, UpdateChecker updates)
    {
        UpdateMessage = "";
        _search = search;
        _navigation = navigation;
        _dispatcher = dispatcher;
        _updates = updates;
        _updates.StateChanged += (_, state) => _dispatcher.TryEnqueue(() => Apply(state));
        Apply(_updates.State);
    }

    /// <summary>Suggestions shown in the title-bar box: recent searches, filtered by what was typed.</summary>
    public ObservableCollection<string> Suggestions { get; } = [];

    public void RefreshSuggestions(string text)
    {
        var needle = text.Trim();
        var items = _search.Recents
            .Where(r => needle.Length == 0 || r.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
            .Take(8)
            .ToList();
        Suggestions.Clear();
        foreach (var item in items)
        {
            Suggestions.Add(item);
        }
    }

    public void Submit(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return;
        }

        if (_navigation.CurrentPage != typeof(SearchPage))
        {
            _navigation.Navigate(typeof(SearchPage));
        }

        _ = _search.SearchAsync(query);
    }

    public void DismissUpdate() => _updates.Dismiss();

    private void Apply(UpdateState state)
    {
        if (state is UpdateState.Available available)
        {
            UpdateMessage = Strings.Format("Update_Available", available.Version);
            UpdatePageUri = Uri.TryCreate(available.PageUrl, UriKind.Absolute, out var uri) ? uri : null;
            IsUpdateAvailable = true;
        }
        else
        {
            IsUpdateAvailable = false;
        }
    }
}
