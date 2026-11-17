using AnimaWindows.App.Services;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AnimaWindows.App.ViewModels;

/// <summary>One row of the episode list: episode, watch progress and download state.</summary>
public sealed class EpisodeRowViewModel : ObservableObject
{
    private EpisodeProgress? _progress;
    private DownloadItem? _download;

    public EpisodeRowViewModel(Episode episode, int index)
    {
        Episode = episode;
        Index = index;
    }

    public Episode Episode { get; }

    public int Index { get; }

    public DownloadItem? Download => _download;

    public string DisplayTitle
    {
        get
        {
            var n = Strings.Format("Details_EpisodeN", Episode.Number);
            return string.IsNullOrWhiteSpace(Episode.Title) ? n : $"{n} - {Episode.Title}";
        }
    }

    public bool IsWatched => _progress?.IsFinished == true;

    public bool HasPartialProgress => _progress is { IsFinished: false, Fraction: > 0f };

    public double PercentValue => (_progress?.Fraction ?? 0f) * 100.0;

    public string ProgressText => IsWatched
        ? Strings.Get("Details_Watched")
        : Strings.Format("Details_PercentWatched", (int)Math.Round(PercentValue));

    public bool ShowProgressText => IsWatched || HasPartialProgress;

    public string DownloadGlyph => _download?.State switch
    {
        DownloadState.Queued => "",
        DownloadState.Downloading => "",
        DownloadState.Paused => "",
        DownloadState.Completed => "",
        DownloadState.Failed => "",
        _ => "",
    };

    public string DownloadTip => _download?.State switch
    {
        DownloadState.Queued => Strings.Get("Details_CancelQueued"),
        DownloadState.Downloading => Strings.Get("Details_PauseDownload"),
        DownloadState.Paused => Strings.Get("Details_ResumeDownload"),
        DownloadState.Completed => Strings.Get("Details_Downloaded"),
        DownloadState.Failed => Strings.Get("Details_DownloadFailedRetry"),
        _ => Strings.Get("Details_DownloadEpisode"),
    };

    public bool CanDownloadAction => _download?.State != DownloadState.Completed;

    public bool ShowDownloadProgress =>
        _download is { State: DownloadState.Downloading or DownloadState.Paused } d && d.Percent > 0f;

    public double DownloadPercent => (_download?.Percent ?? 0f) * 100.0;

    public bool IsDownloadIndeterminate => _download?.State == DownloadState.Queued;

    public void Update(EpisodeProgress? progress)
    {
        _progress = progress;
        OnPropertyChanged(string.Empty);
    }

    public void Update(DownloadItem? download)
    {
        _download = download;
        OnPropertyChanged(string.Empty);
    }
}
