using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimaWindows.App.Pages.Downloads;

/// <summary>One episode row of a download group. Updated in place so progress ticks do not rebuild the list.</summary>
internal sealed partial class DownloadEpisodeViewModel : ObservableObject
{
    private readonly DownloadsViewModel _owner;

    public DownloadEpisodeViewModel(DownloadsViewModel owner, DownloadItem item)
    {
        _owner = owner;
        Item = item;
    }

    public DownloadItem Item { get; private set; }

    public string Key => Item.Key;

    public string EpisodeNumber => Item.EpisodeNumber;

    public string Title => UiText.Format("Downloads_EpisodeFormat", "Episode {0}", Item.EpisodeNumber);

    public bool IsDownloading => Item.State == DownloadState.Downloading;

    public bool IsCompleted => Item.State == DownloadState.Completed;

    public bool IsFailed => Item.State == DownloadState.Failed;

    public bool IsPaused => Item.State == DownloadState.Paused;

    public bool IsQueued => Item.State == DownloadState.Queued;

    /// <summary>Progress bar shown while there is something to show (downloading, or paused part-way).</summary>
    public bool ShowProgress => IsDownloading || (IsPaused && Item.Percent > 0f) || (IsQueued && Item.Percent > 0f);

    public bool IsIndeterminate => IsDownloading && Item.Percent <= 0f;

    /// <summary>0..100.</summary>
    public double ProgressValue => Math.Clamp(Item.Percent * 100.0, 0.0, 100.0);

    public bool CanPause => Item.State is DownloadState.Downloading or DownloadState.Queued;

    public bool CanResume => IsPaused;

    public bool CanRetry => IsFailed;

    public bool CanOpen => Item.IsPlayable;

    public string StatusText => Item.State switch
    {
        DownloadState.Queued => UiText.Get("Downloads_StatusQueued", "Queued"),
        DownloadState.Downloading => UiText.Format(
            "Downloads_StatusDownloading", "{0} - {1}%", UiText.FormatSize(Item.Bytes), (int)Math.Round(ProgressValue)),
        DownloadState.Paused => string.IsNullOrWhiteSpace(Item.FailureReason)
            ? UiText.Get("Downloads_StatusPaused", "Paused")
            : UiText.Format("Downloads_StatusPausedReason", "Paused - {0}", Item.FailureReason),
        DownloadState.Completed => Item.Quality is { } q
            ? $"{UiText.FormatSize(Item.Bytes)} - {q}p"
            : UiText.FormatSize(Item.Bytes),
        DownloadState.Failed => string.IsNullOrWhiteSpace(Item.FailureReason)
            ? UiText.Get("Downloads_StatusFailed", "Failed")
            : UiText.Format("Downloads_StatusFailedReason", "Failed: {0}", Item.FailureReason),
        _ => string.Empty,
    };

    public string AutomationName => $"{Title}, {StatusText}";

    internal void Update(DownloadItem item)
    {
        Item = item;
        OnPropertyChanged(string.Empty);
        PauseCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
        RetryCommand.NotifyCanExecuteChanged();
        OpenCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private void Open() => _owner.Open(this);

    [RelayCommand(CanExecute = nameof(CanPause))]
    private Task PauseAsync() => _owner.PauseAsync(Item);

    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task ResumeAsync() => _owner.ResumeAsync(Item);

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private Task RetryAsync() => _owner.ResumeAsync(Item);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteEpisodeAsync(this);

    [RelayCommand]
    private Task OpenFolderAsync() => _owner.OpenFolderAsync(Item);
}
