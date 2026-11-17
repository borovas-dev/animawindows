using System.Security.Cryptography;
using System.Text;
using AnimaWindows.App.Pages.Downloads;
using AnimaWindows.App.Player;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace AnimaWindows.App.Services.Notifications;

/// <summary>
/// Shows one progress toast per active download, completed / failed toasts with Open / Retry buttons, and mirrors the aggregate
/// progress on the taskbar button. Registered as a hosted service (started by the host) and idempotent via <see cref="Start"/>.
/// Toast activation is routed to <see cref="HandleActivation"/>; the shell only has to forward cold-start toast activations.
/// </summary>
internal sealed class DownloadNotifier : IHostedService, IDisposable
{
    private const string Group = "downloads";
    private const string ArgAction = "action";
    private const string ArgProvider = "p";
    private const string ArgAnime = "a";
    private const string ArgEpisode = "e";
    private const string ActionOpen = "open";
    private const string ActionRetry = "retry";
    private const string ActionShow = "show";
    private static readonly long ProgressUpdateIntervalTicks = TimeSpan.FromSeconds(1).Ticks;

    private readonly IDownloadRepository _repository;
    private readonly IDownloadController _controller;
    private readonly IShellChrome _chrome;
    private readonly IDispatcher _dispatcher;
    private readonly IServiceProvider _services;
    private readonly TaskbarProgress _taskbar = new();
    private readonly Dictionary<string, DownloadItem> _last = [];
    private readonly Dictionary<string, ProgressToast> _progress = [];
    private readonly CancellationTokenSource _cts = new();
    private bool _started;
    private bool _seeded;

    public DownloadNotifier(
        IDownloadRepository repository,
        IDownloadController controller,
        IShellChrome chrome,
        IDispatcher dispatcher,
        IServiceProvider services)
    {
        _repository = repository;
        _controller = controller;
        _chrome = chrome;
        _dispatcher = dispatcher;
        _services = services;
    }

    private sealed class ProgressToast
    {
        public uint Sequence = 1;
        public long LastUpdateTicks;
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        Start();
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>Starts observing downloads. Safe to call more than once.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _ = ObserveAsync(_cts.Token);
    }

    public void Dispose() => _cts.Dispose();

    private async Task ObserveAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var items in _repository.ObserveDownloads(ct).ConfigureAwait(false))
            {
                try
                {
                    await ProcessAsync(items).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    System.Diagnostics.Debug.WriteLine($"DownloadNotifier: {e.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutting down
        }
    }

    private async Task ProcessAsync(IReadOnlyList<DownloadItem> items)
    {
        var present = new HashSet<string>();
        foreach (var item in items)
        {
            present.Add(item.Key);
            _last.TryGetValue(item.Key, out var previous);

            if (item.State == DownloadState.Downloading)
            {
                await ShowOrUpdateProgressAsync(item).ConfigureAwait(false);
            }
            else if (_progress.Remove(item.Key))
            {
                await RemoveToastAsync(item.Key).ConfigureAwait(false);
            }

            if (_seeded && previous is not null && previous.State.IsActive() && item.State != previous.State)
            {
                if (item.State == DownloadState.Completed)
                {
                    ShowCompleted(item);
                }
                else if (item.State == DownloadState.Failed)
                {
                    ShowFailed(item);
                }
            }

            _last[item.Key] = item;
        }

        foreach (var gone in _last.Keys.Where(k => !present.Contains(k)).ToList())
        {
            _last.Remove(gone);
            if (_progress.Remove(gone))
            {
                await RemoveToastAsync(gone).ConfigureAwait(false);
            }
        }

        _seeded = true;
        UpdateTaskbar(items);
    }

    // ---- toasts ----

    private static string TagOf(string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];

    private async Task ShowOrUpdateProgressAsync(DownloadItem item)
    {
        if (!NotificationRegistration.IsRegistered)
        {
            return;
        }

        var tag = TagOf(item.Key);
        var percent = Math.Clamp(item.Percent, 0f, 1f);
        AppNotificationProgressData MakeData(uint sequence) => new(sequence)
        {
            Title = UiText.Format("Downloads_EpisodeFormat", "Episode {0}", item.EpisodeNumber),
            Value = percent,
            ValueStringOverride = $"{(int)Math.Round(percent * 100)}%",
            Status = UiText.Get("Downloads_ToastDownloading", "Downloading"),
        };

        try
        {
            if (!_progress.TryGetValue(item.Key, out var state))
            {
                state = new ProgressToast { LastUpdateTicks = DateTime.UtcNow.Ticks };
                _progress[item.Key] = state;
                var builder = new AppNotificationBuilder()
                    .AddArgument(ArgAction, ActionShow)
                    .AddText(item.Title)
                    .AddProgressBar(new AppNotificationProgressBar()
                        .BindTitle()
                        .BindValue()
                        .BindValueStringOverride()
                        .BindStatus());
                var notification = builder.BuildNotification();
                notification.Tag = tag;
                notification.Group = Group;
                notification.Progress = MakeData(1);
                AppNotificationManager.Default.Show(notification);
                return;
            }

            var now = DateTime.UtcNow.Ticks;
            if (now - state.LastUpdateTicks < ProgressUpdateIntervalTicks)
            {
                return;
            }

            state.LastUpdateTicks = now;
            state.Sequence++;
            await AppNotificationManager.Default.UpdateAsync(MakeData(state.Sequence), tag, Group);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Progress toast failed: {e.Message}");
        }
    }

    private static async Task RemoveToastAsync(string key)
    {
        if (!NotificationRegistration.IsRegistered)
        {
            return;
        }

        try
        {
            await AppNotificationManager.Default.RemoveByTagAndGroupAsync(TagOf(key), Group);
        }
        catch (Exception)
        {
            // best effort
        }
    }

    private static void ShowCompleted(DownloadItem item)
    {
        if (!NotificationRegistration.IsRegistered)
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument(ArgAction, ActionShow)
                .AddText(UiText.Get("Downloads_ToastCompleted", "Download complete"))
                .AddText($"{item.Title} - {UiText.Format("Downloads_EpisodeFormat", "Episode {0}", item.EpisodeNumber)}")
                .AddButton(new AppNotificationButton(UiText.Get("Downloads_ToastOpen", "Open"))
                    .AddArgument(ArgAction, ActionOpen)
                    .AddArgument(ArgProvider, item.ProviderId)
                    .AddArgument(ArgAnime, item.AnimeId)
                    .AddArgument(ArgEpisode, item.EpisodeNumber));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Completed toast failed: {e.Message}");
        }
    }

    private static void ShowFailed(DownloadItem item)
    {
        if (!NotificationRegistration.IsRegistered)
        {
            return;
        }

        try
        {
            var builder = new AppNotificationBuilder()
                .AddArgument(ArgAction, ActionShow)
                .AddText(UiText.Get("Downloads_ToastFailed", "Download failed"))
                .AddText($"{item.Title} - {UiText.Format("Downloads_EpisodeFormat", "Episode {0}", item.EpisodeNumber)}");
            if (!string.IsNullOrWhiteSpace(item.FailureReason))
            {
                builder.AddText(item.FailureReason);
            }

            builder.AddButton(new AppNotificationButton(UiText.Get("Downloads_ToastRetry", "Retry"))
                .AddArgument(ArgAction, ActionRetry)
                .AddArgument(ArgProvider, item.ProviderId)
                .AddArgument(ArgAnime, item.AnimeId)
                .AddArgument(ArgEpisode, item.EpisodeNumber));
            AppNotificationManager.Default.Show(builder.BuildNotification());
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Failed toast failed: {e.Message}");
        }
    }

    // ---- activation ----

    /// <summary>
    /// Handles a toast activation (body click, "Open", "Retry"). Wired to <c>NotificationInvoked</c> by
    /// <see cref="NotificationRegistration"/>; the shell calls it for the cold-start activation
    /// (<c>AppNotificationActivatedEventArgs.Arguments</c>). Safe from any thread.
    /// </summary>
    public void HandleActivation(IDictionary<string, string> arguments) =>
        _ = HandleActivationAsync(new Dictionary<string, string>(arguments));

    private async Task HandleActivationAsync(IReadOnlyDictionary<string, string> arguments)
    {
        try
        {
            arguments.TryGetValue(ArgAction, out var action);
            switch (action)
            {
                case ActionRetry when TryKey(arguments, out var rp, out var ra, out var re):
                    await _controller.ResumeAsync(rp, ra, re).ConfigureAwait(false);
                    await _dispatcher.RunAsync(ShowWindow).ConfigureAwait(false);
                    break;
                case ActionOpen when TryKey(arguments, out var op, out var oa, out var oe):
                    await OpenPlayerAsync(op, oa, oe).ConfigureAwait(false);
                    break;
                default:
                    await _dispatcher.RunAsync(() =>
                    {
                        ShowWindow();
                        _services.GetRequiredService<INavigationService>().Navigate(typeof(DownloadsPage));
                    }).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine($"Toast activation failed: {e.Message}");
        }
    }

    private static bool TryKey(IReadOnlyDictionary<string, string> args, out string provider, out string anime, out string episode)
    {
        var ok = args.TryGetValue(ArgProvider, out var p)
            & args.TryGetValue(ArgAnime, out var a)
            & args.TryGetValue(ArgEpisode, out var e);
        provider = p ?? string.Empty;
        anime = a ?? string.Empty;
        episode = e ?? string.Empty;
        return ok;
    }

    private void ShowWindow()
    {
        try
        {
            _chrome.AppWindow.Show();
        }
        catch (Exception)
        {
            // window may already be gone
        }
    }

    private async Task OpenPlayerAsync(string providerId, string animeId, string episodeNumber)
    {
        var all = await _repository.GetAllAsync().ConfigureAwait(false);
        var playable = all
            .Where(i => i.ProviderId == providerId && i.AnimeId == animeId && i.IsPlayable)
            .OrderBy(i => double.TryParse(i.EpisodeNumber, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : double.MaxValue)
            .ThenBy(i => i.EpisodeNumber, StringComparer.Ordinal)
            .ToList();
        var index = playable.FindIndex(i => i.EpisodeNumber == episodeNumber);
        if (index < 0)
        {
            return;
        }

        var current = playable[index];
        var anime = new Anime { Id = animeId, ProviderId = providerId, Title = current.Title, CoverUrl = current.CoverUrl };
        IReadOnlyList<Episode> episodes = playable.Select(i => new Episode(i.EpisodeId, i.EpisodeNumber)).ToList();
        await _dispatcher.RunAsync(() =>
        {
            ShowWindow();
            _services.GetRequiredService<INavigationService>()
                .Navigate(typeof(PlayerPage), new PlayerNavigationArgs(providerId, anime, episodes, index, Offline: true));
        }).ConfigureAwait(false);
    }

    // ---- taskbar ----

    private void UpdateTaskbar(IReadOnlyList<DownloadItem> items)
    {
        var active = items.Where(i => i.State.IsActive()).ToList();
        var state = TaskbarProgressState.NoProgress;
        ulong completed = 0;
        if (active.Count > 0)
        {
            if (active.Any(i => i.State == DownloadState.Downloading))
            {
                state = TaskbarProgressState.Normal;
                completed = (ulong)Math.Round(active.Average(i => Math.Clamp(i.Percent, 0f, 1f)) * 1000.0);
            }
            else
            {
                state = TaskbarProgressState.Indeterminate;
            }
        }

        var hwnd = _chrome.WindowHandle;
        var total = 1000UL;
        _ = _dispatcher.RunAsync(() => _taskbar.Set(hwnd, state, completed, total));
    }
}
