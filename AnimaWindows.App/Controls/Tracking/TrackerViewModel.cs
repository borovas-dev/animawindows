using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimaWindows.App.Controls.Tracking;

/// <summary>One tracker (AniList or MyAnimeList) row of the tracking section.</summary>
internal sealed partial class TrackerViewModel : ObservableObject
{
    private readonly TrackingViewModel _owner;

    public TrackerViewModel(TrackingViewModel owner, SyncService service, string name)
    {
        _owner = owner;
        Service = service;
        Name = name;
        Update(new ServiceState());
    }

    public SyncService Service { get; }

    public string Name { get; }

    /// <summary>False when the client id is blank: the row is shown disabled.</summary>
    [ObservableProperty]
    public partial bool IsConfigured { get; set; }

    [ObservableProperty]
    public partial bool CanConnect { get; set; }

    [ObservableProperty]
    public partial bool IsConnected { get; set; }

    [ObservableProperty]
    public partial string? StatusText { get; set; }

    internal void Update(ServiceState state)
    {
        IsConfigured = state.Configured;
        IsConnected = state.Configured && state.Connected;
        CanConnect = state.Configured && !state.Connected;
        StatusText = !state.Configured
            ? UiText.Get("Tracking_NotConfigured", "Not configured in this build")
            : state.Connected
                ? UiText.Format("Tracking_ConnectedAs", "Connected as {0}", state.Username ?? string.Empty)
                : UiText.Get("Tracking_NotConnected", "Not connected");
    }

    [RelayCommand]
    private Task ConnectAsync() => _owner.ConnectAsync(Service);

    [RelayCommand]
    private Task DisconnectAsync() => _owner.DisconnectAsync(Service);
}
