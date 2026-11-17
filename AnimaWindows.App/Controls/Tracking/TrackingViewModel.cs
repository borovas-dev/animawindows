using AnimaWindows.App.Services;
using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimaWindows.App.Controls.Tracking;

/// <summary>Mirrors feature/settings TrackingViewModel / TrackingSection.kt over <see cref="ISyncRepository"/>.</summary>
internal sealed partial class TrackingViewModel : ObservableObject
{
    private readonly ISyncRepository _sync;
    private readonly IDispatcher _dispatcher;

    public TrackingViewModel(ISyncRepository sync, IDispatcher dispatcher)
    {
        _sync = sync;
        _dispatcher = dispatcher;
        AniList = new TrackerViewModel(this, SyncService.AniList, "AniList");
        Mal = new TrackerViewModel(this, SyncService.Mal, "MyAnimeList");
        Apply(_sync.State);
    }

    public TrackerViewModel AniList { get; }

    public TrackerViewModel Mal { get; }

    [ObservableProperty]
    public partial bool CanSync { get; set; }

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    [ObservableProperty]
    public partial bool HasPending { get; set; }

    [ObservableProperty]
    public partial string? PendingText { get; set; }

    [ObservableProperty]
    public partial bool HasError { get; set; }

    [ObservableProperty]
    public partial string? ErrorText { get; set; }

    public void Start()
    {
        _sync.StateChanged -= OnStateChanged;
        _sync.StateChanged += OnStateChanged;
        Apply(_sync.State);
    }

    public void Stop() => _sync.StateChanged -= OnStateChanged;

    private void OnStateChanged(object? sender, SyncState state) => _ = _dispatcher.RunAsync(() => Apply(state));

    private void Apply(SyncState state)
    {
        AniList.Update(Get(state, SyncService.AniList));
        Mal.Update(Get(state, SyncService.Mal));
        IsSyncing = state.Syncing;
        CanSync = !state.Syncing && state.Services.Values.Any(s => s.Connected);
        HasPending = state.Pending > 0;
        PendingText = UiText.Format("Tracking_Pending", "{0} pending", state.Pending);
        HasError = !string.IsNullOrWhiteSpace(state.Error);
        ErrorText = state.Error is null ? null : UiText.Format("Tracking_Error", "Last error: {0}", state.Error);
        SyncNowCommand.NotifyCanExecuteChanged();
    }

    private static ServiceState Get(SyncState state, SyncService service) =>
        state.Services.TryGetValue(service, out var s) ? s : new ServiceState();

    internal async Task ConnectAsync(SyncService service)
    {
        var url = _sync.AuthorizeUrl(service);
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return;
        }

        try
        {
            await Windows.System.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception)
        {
            // no browser handler; nothing sensible to do
        }
    }

    internal Task DisconnectAsync(SyncService service) => _sync.DisconnectAsync(service);

    [RelayCommand(CanExecute = nameof(CanSync))]
    private Task SyncNowAsync() => _sync.SyncNowAsync();
}
