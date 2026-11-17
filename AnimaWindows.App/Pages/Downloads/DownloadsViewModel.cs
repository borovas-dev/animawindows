using System.Collections.ObjectModel;
using AnimaWindows.App.Player;
using AnimaWindows.App.Services;
using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimaWindows.App.Pages.Downloads;

/// <summary>
/// Downloads screen state. Mirrors feature/downloads DownloadsScreen: groups per show, per-episode state and actions. The page
/// calls <see cref="Start"/> on load and <see cref="Stop"/> on unload; repository streams are cancelled on stop.
/// </summary>
internal sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly IDownloadRepository _repository;
    private readonly IDownloadController _controller;
    private readonly DownloadManager _manager;
    private readonly IDispatcher _dispatcher;
    private readonly INavigationService _navigation;
    private CancellationTokenSource? _cts;

    public DownloadsViewModel(
        IDownloadRepository repository,
        IDownloadController controller,
        DownloadManager manager,
        IDispatcher dispatcher,
        INavigationService navigation)
    {
        _repository = repository;
        _controller = controller;
        _manager = manager;
        _dispatcher = dispatcher;
        _navigation = navigation;
    }

    public ObservableCollection<DownloadGroupViewModel> Groups { get; } = [];

    [ObservableProperty]
    public partial bool IsLoaded { get; set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; set; }

    [ObservableProperty]
    public partial bool IsWaitingForNetwork { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    /// <summary>Set by the page: asks the user (title, message, confirm label) and returns true on confirm.</summary>
    public Func<string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        IsWaitingForNetwork = _manager.WaitingForNetwork;
        _manager.WaitingForNetworkChanged += OnWaitingForNetworkChanged;
        _manager.Message += OnManagerMessage;
        _ = ObserveAsync(cts.Token);
    }

    public void Stop()
    {
        _manager.WaitingForNetworkChanged -= OnWaitingForNetworkChanged;
        _manager.Message -= OnManagerMessage;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ObserveAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var groups in _repository.ObserveGroupedByAnime(ct).ConfigureAwait(false))
            {
                await _dispatcher.RunAsync(() => Apply(groups)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // page left
        }
        catch (Exception e)
        {
            await _dispatcher.RunAsync(() => ShowError(e.Message)).ConfigureAwait(false);
        }
    }

    private void OnWaitingForNetworkChanged(object? sender, bool waiting) =>
        _ = _dispatcher.RunAsync(() => IsWaitingForNetwork = waiting);

    private void OnManagerMessage(object? sender, string message) =>
        _ = _dispatcher.RunAsync(() => ShowError(message));

    private void ShowError(string message)
    {
        ErrorMessage = message;
        HasError = true;
    }

    private void Apply(IReadOnlyList<DownloadGroup> groups)
    {
        for (var i = 0; i < groups.Count; i++)
        {
            var group = groups[i];
            var key = $"{group.ProviderId}/{group.AnimeId}";
            var existing = -1;
            for (var j = i; j < Groups.Count; j++)
            {
                if (Groups[j].Key == key)
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                Groups.Insert(i, new DownloadGroupViewModel(this, group));
            }
            else
            {
                if (existing != i)
                {
                    Groups.Move(existing, i);
                }

                Groups[i].Apply(group);
            }
        }

        while (Groups.Count > groups.Count)
        {
            Groups.RemoveAt(Groups.Count - 1);
        }

        IsLoaded = true;
        IsEmpty = Groups.Count == 0;
    }

    // ---- actions (called by row/group view models) ----

    internal void Open(DownloadEpisodeViewModel episode)
    {
        var group = Groups.FirstOrDefault(g => g.Episodes.Contains(episode));
        if (group is null || !episode.CanOpen)
        {
            return;
        }

        var playable = group.Episodes.Where(e => e.CanOpen).ToList();
        var index = playable.IndexOf(episode);
        if (index < 0)
        {
            return;
        }

        var anime = new Anime
        {
            Id = group.AnimeId,
            ProviderId = group.ProviderId,
            Title = group.Title,
            CoverUrl = episode.Item.CoverUrl,
        };
        IReadOnlyList<Episode> episodes = playable.Select(e => new Episode(e.Item.EpisodeId, e.Item.EpisodeNumber)).ToList();
        _navigation.Navigate(typeof(PlayerPage), new PlayerNavigationArgs(group.ProviderId, anime, episodes, index, Offline: true));
    }

    internal Task PauseAsync(DownloadItem item) =>
        RunAsync(() => _controller.PauseAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber));

    /// <summary>Resume of a paused download, or retry of a failed one (the manager re-resolves the stream).</summary>
    internal Task ResumeAsync(DownloadItem item) =>
        RunAsync(() => _controller.ResumeAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber));

    internal async Task DeleteEpisodeAsync(DownloadEpisodeViewModel episode)
    {
        var confirm = ConfirmAsync;
        if (confirm is not null)
        {
            var ok = await confirm(
                UiText.Get("Downloads_DeleteTitle", "Delete download?"),
                UiText.Format("Downloads_DeleteEpisodeMessage", "Episode {0} will be removed from this device.", episode.EpisodeNumber),
                UiText.Get("Downloads_DeleteConfirm", "Delete"));
            if (!ok)
            {
                return;
            }
        }

        var item = episode.Item;
        await RunAsync(() => _controller.RemoveAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber));
    }

    internal async Task DeleteGroupAsync(DownloadGroupViewModel group)
    {
        var confirm = ConfirmAsync;
        if (confirm is not null)
        {
            var ok = await confirm(
                UiText.Get("Downloads_DeleteTitle", "Delete download?"),
                UiText.Format("Downloads_DeleteGroupMessage", "All downloaded episodes of {0} will be removed from this device.", group.Title),
                UiText.Get("Downloads_DeleteConfirm", "Delete"));
            if (!ok)
            {
                return;
            }
        }

        var items = group.Episodes.Select(e => e.Item).ToList();
        await RunAsync(async () =>
        {
            foreach (var item in items)
            {
                await _controller.RemoveAsync(item.ProviderId, item.AnimeId, item.EpisodeNumber);
            }
        });
    }

    internal async Task OpenFolderAsync(DownloadItem item)
    {
        try
        {
            var dir = DownloadIds.EpisodeDirectory(_manager.RootDirectory, item.ProviderId, item.AnimeId, item.EpisodeNumber);
            if (!Directory.Exists(dir))
            {
                dir = _manager.RootDirectory;
            }

            if (Directory.Exists(dir))
            {
                await Windows.System.Launcher.LaunchFolderPathAsync(dir);
            }
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
            // ignored
        }
        catch (Exception e)
        {
            ShowError(e.Message);
        }
    }
}
