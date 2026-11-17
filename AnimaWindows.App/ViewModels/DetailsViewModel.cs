using System.Collections.ObjectModel;
using AnimaWindows.App.Player;
using AnimaWindows.App.Services;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using AnimaWindows.Core.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimaWindows.App.ViewModels;

/// <summary>Show details: metadata, sub/dub, episodes with progress and downloads, play/favourite actions.</summary>
public sealed partial class DetailsViewModel : ObservableObject
{
    private const int CollapsedSynopsisLines = 4;
    private const int LongSynopsisLength = 220;

    private readonly IProviderChain _chain;
    private readonly ILibraryRepository _library;
    private readonly ISettingsStore _settings;
    private readonly IDownloadRepository _downloads;
    private readonly IDownloadController _controller;
    private readonly INavigationService _navigation;
    private readonly ICloudflareSolver _solver;

    private Anime? _initial;
    private CancellationToken _lifetime;
    private CancellationTokenSource? _observeCts;
    private CancellationTokenSource? _loadCts;
    private string _observedKey = "";
    private List<Episode> _episodes = [];
    private IReadOnlyList<TranslationMode> _modes = (TranslationMode[])[TranslationMode.Sub, TranslationMode.Dub];
    private Dictionary<string, EpisodeProgress> _progress = [];
    private Dictionary<string, DownloadItem> _downloadItems = [];
    private PlayTarget? _target;
    private bool _syncingMode;

    [ObservableProperty]
    public partial Anime? Anime { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; }

    [ObservableProperty]
    public partial string? CloudflareUrl { get; set; }

    [ObservableProperty]
    public partial bool IsFavorite { get; set; }

    [ObservableProperty]
    public partial bool IsSynopsisExpanded { get; set; }

    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    public DetailsViewModel(
        IProviderChain chain,
        ILibraryRepository library,
        ISettingsStore settings,
        IDownloadRepository downloads,
        IDownloadController controller,
        INavigationService navigation,
        ICloudflareSolver solver)
    {
        IsLoading = true;
        ErrorMessage = "";
        _chain = chain;
        _library = library;
        _settings = settings;
        _downloads = downloads;
        _controller = controller;
        _navigation = navigation;
        _solver = solver;
    }

    public ObservableCollection<EpisodeRowViewModel> Episodes { get; } = [];

    public string Title => Anime?.Title ?? "";

    public string? CoverUrl => Anime?.CoverUrl;

    public string? Synopsis => Anime?.Synopsis;

    public bool HasSynopsis => !string.IsNullOrWhiteSpace(Anime?.Synopsis);

    public bool CanExpandSynopsis => (Anime?.Synopsis?.Length ?? 0) > LongSynopsisLength;

    public int SynopsisMaxLines => IsSynopsisExpanded ? 0 : CollapsedSynopsisLines;

    public string SynopsisToggleText => Strings.Get(IsSynopsisExpanded ? "Details_ShowLess" : "Details_ReadMore");

    public string ProviderName => Anime is null ? "" : _chain.Provider(Anime.ProviderId)?.DisplayName ?? Anime.ProviderId;

    public string CountsText
    {
        get
        {
            var parts = new List<string>();
            if (Anime?.SubEpisodes is { } sub)
            {
                parts.Add(Strings.Format("Details_SubEpisodes", sub));
            }

            if (Anime?.DubEpisodes is { } dub)
            {
                parts.Add(Strings.Format("Details_DubEpisodes", dub));
            }

            if (Anime?.Year is { } year)
            {
                parts.Add(year.ToString());
            }

            return string.Join("  ·  ", parts);
        }
    }

    public bool ShowModeToggle => _modes.Count > 1;

    public TranslationMode Mode => ModeIndex == 1 ? TranslationMode.Dub : TranslationMode.Sub;

    public bool HasError => !IsLoading && ErrorMessage.Length > 0;

    public bool NeedsVerification => HasError && CloudflareUrl is not null;

    public bool ShowEmpty => !IsLoading && !HasError && Episodes.Count == 0;

    public bool ShowEpisodes => Episodes.Count > 0;

    public string EmptyBody => Strings.Format("Details_NoEpisodesBody", Strings.Get(Mode == TranslationMode.Dub ? "Audio_Dub" : "Audio_Sub"));

    public string EpisodesHeader => Strings.Format("Details_EpisodesCount", Episodes.Count);

    public int EpisodeCount => Episodes.Count;

    public bool CanPlay => _target is not null;

    public string PlayLabel => _target is null
        ? Strings.Get("Details_Play")
        : Strings.Format(_target.Kind switch
        {
            PlayTargetKind.Continue => "Details_ContinueEp",
            PlayTargetKind.Replay => "Details_ReplayEp",
            _ => "Details_PlayEp",
        }, _target.Episode);

    public string FavoriteGlyph => IsFavorite ? "" : "";

    public string FavoriteTip => Strings.Get(IsFavorite ? "Details_RemoveFavorite" : "Details_AddFavorite");

    /// <summary>Sets the show to display; call once before <see cref="RunAsync"/>.</summary>
    public void Initialize(Anime anime)
    {
        if (_initial is not null)
        {
            return;
        }

        _initial = anime;
        Anime = anime;
    }

    partial void OnAnimeChanged(Anime? value)
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(CoverUrl));
        OnPropertyChanged(nameof(Synopsis));
        OnPropertyChanged(nameof(HasSynopsis));
        OnPropertyChanged(nameof(CanExpandSynopsis));
        OnPropertyChanged(nameof(ProviderName));
        OnPropertyChanged(nameof(CountsText));
    }

    partial void OnIsLoadingChanged(bool value) => RaiseStates();

    partial void OnErrorMessageChanged(string value) => RaiseStates();

    partial void OnCloudflareUrlChanged(string? value) => RaiseStates();

    partial void OnIsFavoriteChanged(bool value)
    {
        OnPropertyChanged(nameof(FavoriteGlyph));
        OnPropertyChanged(nameof(FavoriteTip));
    }

    partial void OnIsSynopsisExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(SynopsisMaxLines));
        OnPropertyChanged(nameof(SynopsisToggleText));
    }

    partial void OnModeIndexChanged(int value)
    {
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(EmptyBody));
        if (_syncingMode || Anime is null)
        {
            return;
        }

        var mode = Mode;
        _ = _settings.UpdateAsync(s => s with { Mode = mode });
        _ = ReloadEpisodesAsync(mode);
    }

    private void RaiseStates()
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(NeedsVerification));
        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(ShowEpisodes));
    }

    /// <summary>Loads details and episodes and observes progress, downloads and favourite until cancelled.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _lifetime = cancellationToken;
        StartObserving();
        await LoadAsync();
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private async Task VerifyAsync()
    {
        if (CloudflareUrl is not null && Uri.TryCreate(CloudflareUrl, UriKind.Absolute, out var uri) && await _solver.SolveAsync(uri))
        {
            await LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        if (_initial is null)
        {
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var ct = cts.Token;
        IsLoading = true;
        ErrorMessage = "";
        CloudflareUrl = null;
        try
        {
            var provider = _chain.Provider(_initial.ProviderId) ?? (await _chain.ChainAsync(ct)).FirstOrDefault();
            if (provider is null)
            {
                Fail(new NoProviderAvailableException("No provider"));
                return;
            }

            var enriched = _initial;
            try
            {
                enriched = await provider.DetailsAsync(_initial, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Metadata is optional; the search result is enough to continue.
            }

            if (enriched.Alternates.Count == 0)
            {
                enriched = enriched with { Alternates = _initial.Alternates };
            }

            SetAnime(enriched);
            _modes = AvailableModes(enriched);
            var preferred = _settings.Current.Mode;
            var mode = _modes.Contains(preferred) ? preferred : _modes[0];
            _syncingMode = true;
            ModeIndex = mode == TranslationMode.Dub ? 1 : 0;
            _syncingMode = false;
            OnPropertyChanged(nameof(ShowModeToggle));
            await FetchEpisodesAsync(provider, enriched, mode, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private async Task ReloadEpisodesAsync(TranslationMode mode)
    {
        if (Anime is null)
        {
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var ct = cts.Token;
        IsLoading = true;
        ErrorMessage = "";
        CloudflareUrl = null;
        SetEpisodes([]);
        try
        {
            var provider = _chain.Provider(Anime.ProviderId) ?? (await _chain.ChainAsync(ct)).FirstOrDefault();
            if (provider is null)
            {
                Fail(new NoProviderAvailableException("No provider"));
                return;
            }

            await FetchEpisodesAsync(provider, Anime, mode, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Fail(e);
        }
    }

    private async Task FetchEpisodesAsync(IAnimeProvider provider, Anime anime, TranslationMode mode, CancellationToken ct)
    {
        Exception? primaryFailure = null;
        IReadOnlyList<Episode> primary = [];
        try
        {
            primary = await provider.EpisodesAsync(anime, mode, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            primaryFailure = e;
        }

        if (primary.Count > 0)
        {
            SetEpisodes(primary);
            IsLoading = false;
            return;
        }

        // The provider knows the show but has nothing playable: try the alternates (same show on other providers).
        foreach (var (altProviderId, altId) in anime.Alternates)
        {
            var alt = _chain.Provider(altProviderId);
            if (alt is null)
            {
                continue;
            }

            var alternates = anime.Alternates.Where(kv => kv.Key != altProviderId).ToDictionary(kv => kv.Key, kv => kv.Value);
            alternates[anime.ProviderId] = anime.Id;
            var altAnime = anime with { Id = altId, ProviderId = altProviderId, Alternates = alternates };
            try
            {
                var episodes = await alt.EpisodesAsync(altAnime, mode, ct);
                if (episodes.Count > 0)
                {
                    SetAnime(altAnime);
                    SetEpisodes(episodes);
                    IsLoading = false;
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Try the next alternate.
            }
        }

        if (primaryFailure is not null)
        {
            Fail(primaryFailure);
        }
        else
        {
            SetEpisodes([]);
            IsLoading = false;
        }
    }

    private void Fail(Exception exception)
    {
        var info = ErrorInfo.From(exception, "Details_LoadFailed");
        CloudflareUrl = info.CloudflareUrl;
        ErrorMessage = info.Message;
        IsLoading = false;
    }

    private static IReadOnlyList<TranslationMode> AvailableModes(Anime anime)
    {
        if (anime.SubEpisodes is null && anime.DubEpisodes is null)
        {
            return (TranslationMode[])[TranslationMode.Sub, TranslationMode.Dub];
        }

        var modes = new List<TranslationMode>();
        if ((anime.SubEpisodes ?? 0) > 0)
        {
            modes.Add(TranslationMode.Sub);
        }

        if ((anime.DubEpisodes ?? 0) > 0)
        {
            modes.Add(TranslationMode.Dub);
        }

        return modes.Count == 0 ? [TranslationMode.Sub, TranslationMode.Dub] : modes;
    }

    private void SetAnime(Anime anime)
    {
        Anime = anime;
        StartObserving();
    }

    private void SetEpisodes(IReadOnlyList<Episode> episodes)
    {
        _episodes = [.. episodes];
        Episodes.Clear();
        for (var i = 0; i < _episodes.Count; i++)
        {
            var row = new EpisodeRowViewModel(_episodes[i], i);
            row.Update(_progress.GetValueOrDefault(_episodes[i].Number));
            row.Update(_downloadItems.GetValueOrDefault(_episodes[i].Number));
            Episodes.Add(row);
        }

        UpdateTarget();
        RaiseStates();
        OnPropertyChanged(nameof(EpisodesHeader));
        OnPropertyChanged(nameof(EpisodeCount));
        OnPropertyChanged(nameof(EmptyBody));
    }

    private void UpdateTarget()
    {
        _target = PlayTarget.Compute(_episodes, _progress.Values);
        OnPropertyChanged(nameof(CanPlay));
        OnPropertyChanged(nameof(PlayLabel));
    }

    // ---- observation (progress, downloads, favourite) keyed by the current provider/anime id ----

    private void StartObserving()
    {
        if (Anime is null)
        {
            return;
        }

        var key = $"{Anime.ProviderId}/{Anime.Id}";
        if (key == _observedKey)
        {
            return;
        }

        _observedKey = key;
        _observeCts?.Cancel();
        var cts = _observeCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime);
        var ct = cts.Token;
        var providerId = Anime.ProviderId;
        var animeId = Anime.Id;
        _ = ObserveProgressAsync(providerId, animeId, ct);
        _ = ObserveDownloadsAsync(providerId, animeId, ct);
        _ = ObserveFavoriteAsync(providerId, animeId, ct);
    }

    private async Task ObserveProgressAsync(string providerId, string animeId, CancellationToken ct)
    {
        try
        {
            await foreach (var list in _library.ObserveProgressForAnime(providerId, animeId, ct))
            {
                _progress = list.ToDictionary(p => p.EpisodeNumber);
                foreach (var row in Episodes)
                {
                    row.Update(_progress.GetValueOrDefault(row.Episode.Number));
                }

                UpdateTarget();
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ObserveDownloadsAsync(string providerId, string animeId, CancellationToken ct)
    {
        try
        {
            await foreach (var map in _downloads.ObserveForAnime(providerId, animeId, ct))
            {
                _downloadItems = new Dictionary<string, DownloadItem>(map);
                foreach (var row in Episodes)
                {
                    row.Update(_downloadItems.GetValueOrDefault(row.Episode.Number));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task ObserveFavoriteAsync(string providerId, string animeId, CancellationToken ct)
    {
        try
        {
            await foreach (var favorite in _library.ObserveIsFavorite(providerId, animeId, ct))
            {
                IsFavorite = favorite;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- actions ----

    [RelayCommand]
    private void Play()
    {
        if (_target is not null)
        {
            PlayAt(_target.Index);
        }
    }

    public void PlayAt(int index)
    {
        if (Anime is null || index < 0 || index >= _episodes.Count)
        {
            return;
        }

        _navigation.Navigate(
            typeof(PlayerPage),
            new PlayerNavigationArgs(Anime.ProviderId, Anime, _episodes, index, false));
    }

    [RelayCommand]
    private async Task ToggleFavoriteAsync()
    {
        if (Anime is not null)
        {
            await _library.SetFavoriteAsync(Anime, !IsFavorite);
        }
    }

    [RelayCommand]
    private void ToggleSynopsis() => IsSynopsisExpanded = !IsSynopsisExpanded;

    public async Task DownloadActionAsync(EpisodeRowViewModel row)
    {
        if (Anime is null)
        {
            return;
        }

        var item = row.Download;
        switch (item?.State)
        {
            case null:
                await _controller.EnqueueAsync(Anime, row.Episode, Mode);
                break;
            case DownloadState.Queued:
                await _controller.RemoveAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber);
                break;
            case DownloadState.Downloading:
                await _controller.PauseAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber);
                break;
            case DownloadState.Paused:
            case DownloadState.Failed:
                await _controller.ResumeAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber);
                break;
        }
    }

    /// <summary>Queues episodes <paramref name="from"/>..<paramref name="to"/> (1-based positions, inclusive) not yet downloaded.</summary>
    public async Task DownloadRangeAsync(int from, int to)
    {
        if (Anime is null || _episodes.Count == 0)
        {
            return;
        }

        var lo = Math.Clamp(from - 1, 0, _episodes.Count - 1);
        var hi = Math.Clamp(to - 1, lo, _episodes.Count - 1);
        var todo = _episodes
            .Skip(lo)
            .Take(hi - lo + 1)
            .Where(e => !_downloadItems.TryGetValue(e.Number, out var d) || d.State == DownloadState.Failed)
            .ToList();
        if (todo.Count > 0)
        {
            await _controller.EnqueueAllAsync(Anime, todo, Mode);
        }
    }
}
