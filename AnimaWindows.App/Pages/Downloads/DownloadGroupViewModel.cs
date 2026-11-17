using System.Collections.ObjectModel;
using AnimaWindows.App.Services.Notifications;
using AnimaWindows.Core.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnimaWindows.App.Pages.Downloads;

/// <summary>One show card: cover, title, total size and its episode rows.</summary>
internal sealed partial class DownloadGroupViewModel : ObservableObject
{
    private readonly DownloadsViewModel _owner;
    private string? _coverUrl;

    public DownloadGroupViewModel(DownloadsViewModel owner, DownloadGroup group)
    {
        _owner = owner;
        ProviderId = group.ProviderId;
        AnimeId = group.AnimeId;
        Title = group.Title;
        Apply(group);
    }

    public string ProviderId { get; }

    public string AnimeId { get; }

    public string Key => $"{ProviderId}/{AnimeId}";

    public string Title { get; private set; }

    public ObservableCollection<DownloadEpisodeViewModel> Episodes { get; } = [];

    [ObservableProperty]
    public partial ImageSource? Cover { get; set; }

    [ObservableProperty]
    public partial string? SummaryText { get; set; }

    public string AutomationName => $"{Title}, {SummaryText}";

    internal void Apply(DownloadGroup group)
    {
        Title = group.Title;
        if (_coverUrl != group.CoverUrl)
        {
            _coverUrl = group.CoverUrl;
            Cover = Uri.TryCreate(_coverUrl, UriKind.Absolute, out var uri)
                ? new BitmapImage(uri) { DecodePixelWidth = 160 }
                : null;
        }

        SummaryText = UiText.Format(
            "Downloads_GroupSummary", "{0} episodes - {1}", group.Items.Count, UiText.FormatSize(group.TotalBytes));

        // Reconcile episodes in place (same order as the repository: numeric episode number).
        var wanted = group.Items;
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var existing = -1;
            for (var j = i; j < Episodes.Count; j++)
            {
                if (Episodes[j].Key == item.Key)
                {
                    existing = j;
                    break;
                }
            }

            if (existing < 0)
            {
                Episodes.Insert(i, new DownloadEpisodeViewModel(_owner, item));
            }
            else
            {
                if (existing != i)
                {
                    Episodes.Move(existing, i);
                }

                Episodes[i].Update(item);
            }
        }

        while (Episodes.Count > wanted.Count)
        {
            Episodes.RemoveAt(Episodes.Count - 1);
        }

        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(AutomationName));
    }

    [RelayCommand]
    private Task DeleteAllAsync() => _owner.DeleteGroupAsync(this);
}
