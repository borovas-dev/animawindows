using System.Collections.ObjectModel;
using AnimaWindows.App.Pages.Details;
using AnimaWindows.App.Services;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace AnimaWindows.App.ViewModels;

public enum SearchState
{
    Idle,
    Loading,
    Success,
    Error,
}

/// <summary>Search results, recent searches and sub/dub mode. A singleton so the results survive navigation (like the Android app).</summary>
public sealed partial class SearchViewModel : ObservableObject
{
    private readonly IProviderChain _chain;
    private readonly ILibraryRepository _library;
    private readonly ISettingsStore _settings;
    private readonly ICloudflareSolver _solver;
    private readonly IDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private CancellationTokenSource? _cts;
    private bool _syncingMode;

    [ObservableProperty]
    public partial string Query { get; set; }

    [ObservableProperty]
    public partial SearchState State { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? CloudflareUrl { get; set; }

    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    public SearchViewModel(
        IProviderChain chain,
        ILibraryRepository library,
        ISettingsStore settings,
        ICloudflareSolver solver,
        IDispatcher dispatcher,
        INavigationService navigation)
    {
        Query = "";
        ErrorMessage = "";
        State = SearchState.Idle;
        _chain = chain;
        _library = library;
        _settings = settings;
        _solver = solver;
        _dispatcher = dispatcher;
        _navigation = navigation;
        _syncingMode = true;
        ModeIndex = settings.Current.Mode == TranslationMode.Dub ? 1 : 0;
        _syncingMode = false;
        _settings.Changed += (_, s) => _dispatcher.TryEnqueue(() =>
        {
            _syncingMode = true;
            ModeIndex = s.Mode == TranslationMode.Dub ? 1 : 0;
            _syncingMode = false;
        });
        Recents.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRecents));
    }

    public ObservableCollection<Anime> Results { get; } = [];

    public ObservableCollection<string> Recents { get; } = [];

    public bool IsIdle => State == SearchState.Idle;

    public bool IsLoading => State == SearchState.Loading;

    public bool HasResults => State == SearchState.Success && Results.Count > 0;

    public bool IsEmptyResult => State == SearchState.Success && Results.Count == 0;

    public bool HasError => State == SearchState.Error;

    public bool NeedsVerification => HasError && CloudflareUrl is not null;

    public bool HasRecents => Recents.Count > 0;

    public bool ShowRecents => IsIdle && HasRecents;

    public bool ShowIdleHint => IsIdle && !HasRecents;

    public string NoResultsBody =>
        Strings.Format("Search_NoResultsBody", Query.Trim(), Strings.Get(ModeIndex == 1 ? "Audio_Dub" : "Audio_Sub"));

    private TranslationMode CurrentMode => ModeIndex == 1 ? TranslationMode.Dub : TranslationMode.Sub;

    partial void OnStateChanged(SearchState value) => RaiseDerived();

    partial void OnCloudflareUrlChanged(string? value) => RaiseDerived();

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(NoResultsBody));
        if (_syncingMode)
        {
            return;
        }

        var mode = CurrentMode;
        _ = _settings.UpdateAsync(s => s with { Mode = mode });
        if (!string.IsNullOrWhiteSpace(Query) && State != SearchState.Idle)
        {
            _ = RunSearchAsync(Query.Trim());
        }
    }

    private void RaiseDerived()
    {
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(IsEmptyResult));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(NeedsVerification));
        OnPropertyChanged(nameof(ShowRecents));
        OnPropertyChanged(nameof(ShowIdleHint));
        OnPropertyChanged(nameof(NoResultsBody));
    }

    /// <summary>Observes recent searches for the lifetime of the app (also feeds the title-bar suggestions).</summary>
    public async Task RunRecentsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var list in _library.ObserveRecentSearches(20, cancellationToken))
            {
                Recents.Clear();
                foreach (var item in list)
                {
                    Recents.Add(item);
                }

                OnPropertyChanged(nameof(ShowRecents));
                OnPropertyChanged(nameof(ShowIdleHint));
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async Task SearchAsync(string raw)
    {
        var q = raw.Trim();
        Query = raw;
        if (q.Length == 0)
        {
            return;
        }

        _ = _library.AddSearchAsync(q);
        await RunSearchAsync(q);
    }

    private async Task RunSearchAsync(string q)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var ct = cts.Token;
        State = SearchState.Loading;
        Results.Clear();
        try
        {
            var settings = _settings.Current;
            if (_chain.ChainFor(settings).Count == 0)
            {
                SetError(new ErrorInfo(Strings.Get("Error_NoProvider")));
                return;
            }

            var mode = CurrentMode;
            var found = await _chain.SearchAsync(q, mode, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            foreach (var anime in found)
            {
                if (mode == TranslationMode.Dub && !IsDubVisible(anime))
                {
                    continue;
                }

                Results.Add(anime);
            }

            State = SearchState.Success;
            RaiseDerived();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            if (!ct.IsCancellationRequested)
            {
                SetError(ErrorInfo.From(e, "Search_Failed"));
            }
        }
    }

    /// <summary>In DUB mode only shows with a recorded dub are listed, unless the provider cannot tell.</summary>
    private bool IsDubVisible(Anime anime) =>
        _chain.Provider(anime.ProviderId)?.ReportsDubAvailability != true || (anime.DubEpisodes ?? 0) > 0;

    private void SetError(ErrorInfo error)
    {
        ErrorMessage = error.Message;
        CloudflareUrl = error.CloudflareUrl;
        State = SearchState.Error;
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (!string.IsNullOrWhiteSpace(Query))
        {
            await RunSearchAsync(Query.Trim());
        }
    }

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (CloudflareUrl is null || !Uri.TryCreate(CloudflareUrl, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            if (await _solver.SolveAsync(uri))
            {
                await RetryAsync();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // An async command's exception would otherwise surface on the UI thread and end the app.
            SetError(ErrorInfo.From(e, "Search_Failed"));
        }
    }

    public Task RemoveRecentAsync(string query) => _library.RemoveSearchAsync(query);

    [RelayCommand]
    private Task ClearRecentsAsync() => _library.ClearSearchesAsync();

    public void OpenAnime(Anime anime) =>
        _navigation.Navigate(typeof(DetailsPage), anime, new SuppressNavigationTransitionInfo());
}
