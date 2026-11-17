using System.Collections.ObjectModel;
using AnimaWindows.App.Pages.Details;
using AnimaWindows.App.Pages.Search;
using AnimaWindows.App.Services;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media.Animation;

namespace AnimaWindows.App.ViewModels;

/// <summary>One "Continue watching" card.</summary>
public sealed class ContinueCard
{
    public ContinueCard(WatchEntry entry) => Entry = entry;

    public WatchEntry Entry { get; }

    public Anime Anime => Entry.Anime;

    public string Title => Entry.Anime.Title;

    public string? CoverUrl => Entry.Anime.CoverUrl;

    public string EpisodeText => Strings.Format("Library_EpisodeN", Entry.Last.EpisodeNumber);

    /// <summary>0..100.</summary>
    public double Percent => Entry.Last.Fraction * 100.0;
}

public sealed partial class HomeViewModel : ObservableObject
{
    private readonly ILibraryRepository _library;
    private readonly INavigationService _navigation;
    private bool _continueLoaded;
    private bool _favoritesLoaded;

    public HomeViewModel(ILibraryRepository library, INavigationService navigation)
    {
        _library = library;
        _navigation = navigation;
    }

    public ObservableCollection<ContinueCard> ContinueWatching { get; } = [];

    public ObservableCollection<Anime> Favorites { get; } = [];

    public bool IsLoading => !_continueLoaded || !_favoritesLoaded;

    public bool IsLoaded => !IsLoading;

    public bool HasContinue => ContinueWatching.Count > 0;

    public bool HasFavorites => Favorites.Count > 0;

    public bool IsEmpty => IsLoaded && !HasContinue && !HasFavorites;

    /// <summary>Observes both library streams until cancelled (call from the UI thread; results are applied on it).</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(ObserveContinueAsync(cancellationToken), ObserveFavoritesAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            // Observed from a fire-and-forget call: log, and stop showing the skeleton forever.
            System.Diagnostics.Debug.WriteLine($"Home library observation failed: {e.Message}");
            _continueLoaded = true;
            _favoritesLoaded = true;
            RaiseState();
        }
    }

    private async Task ObserveContinueAsync(CancellationToken ct)
    {
        await foreach (var entries in _library.ObserveContinueWatching(ct))
        {
            ContinueWatching.Clear();
            foreach (var entry in entries)
            {
                ContinueWatching.Add(new ContinueCard(entry));
            }

            _continueLoaded = true;
            RaiseState();
        }
    }

    private async Task ObserveFavoritesAsync(CancellationToken ct)
    {
        await foreach (var favorites in _library.ObserveFavorites(ct))
        {
            Favorites.Clear();
            foreach (var anime in favorites)
            {
                Favorites.Add(anime);
            }

            _favoritesLoaded = true;
            RaiseState();
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(IsLoading));
        OnPropertyChanged(nameof(IsLoaded));
        OnPropertyChanged(nameof(HasContinue));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void OpenAnime(Anime anime) =>
        _navigation.Navigate(typeof(DetailsPage), anime, new SuppressNavigationTransitionInfo());

    public void GoToSearch() => _navigation.Navigate(typeof(SearchPage));

    public Task RemoveFromHistoryAsync(ContinueCard card) =>
        _library.RemoveFromHistoryAsync(card.Anime.ProviderId, card.Anime.Id);
}
