using AnimaWindows.App.Controls;
using AnimaWindows.App.Services;
using AnimaWindows.App.ViewModels;
using AnimaWindows.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AnimaWindows.App.Pages.Details;

public sealed partial class DetailsPage : Page
{
    private CancellationTokenSource? _cts;

    public DetailsPage()
    {
        ViewModel = AppServices.Get<DetailsViewModel>();
        InitializeComponent();
    }

    public DetailsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is Anime anime)
        {
            ViewModel.Initialize(anime);
        }

        void StartAnimation(object? sender, RoutedEventArgs args)
        {
            Loaded -= StartAnimation;
            PosterTransition.Start(HeroPoster);
        }

        Loaded += StartAnimation;
        _cts = new CancellationTokenSource();
        _ = ViewModel.RunAsync(_cts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
        _cts = null;
    }

    private void OnEpisodeClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is EpisodeRowViewModel row)
        {
            ViewModel.PlayAt(row.Index);
        }
    }

    private void OnEpisodeDownloadClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: EpisodeRowViewModel row })
        {
            _ = ViewModel.DownloadActionAsync(row);
        }
    }

    private async void OnDownloadRangeClick(object sender, RoutedEventArgs e)
    {
        var count = ViewModel.EpisodeCount;
        if (count == 0)
        {
            return;
        }

        var from = new NumberBox
        {
            Header = Strings.Get("Details_RangeFrom"),
            Minimum = 1,
            Maximum = count,
            Value = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            AcceptsExpression = false,
        };
        var to = new NumberBox
        {
            Header = Strings.Get("Details_RangeTo"),
            Minimum = 1,
            Maximum = count,
            Value = count,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            AcceptsExpression = false,
        };
        var numbers = new Grid { ColumnSpacing = 12 };
        numbers.ColumnDefinitions.Add(new ColumnDefinition());
        numbers.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(to, 1);
        numbers.Children.Add(from);
        numbers.Children.Add(to);

        var content = new StackPanel { Spacing = 16 };
        content.Children.Add(new TextBlock
        {
            Text = Strings.Format("Details_RangeBody", count),
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(numbers);

        var dialog = new ContentDialog
        {
            Title = Strings.Get("Details_DownloadEpisodes"),
            Content = content,
            PrimaryButtonText = Strings.Get("Common_Download"),
            CloseButtonText = Strings.Get("Common_Cancel"),
            DefaultButton = ContentDialogButton.Primary,
        };

        var result = await AppServices.Get<IDialogService>().ShowAsync(dialog);
        if (result == ContentDialogResult.Primary)
        {
            var lo = double.IsNaN(from.Value) ? 1 : (int)from.Value;
            var hi = double.IsNaN(to.Value) ? count : (int)to.Value;
            await ViewModel.DownloadRangeAsync(lo, hi);
        }
    }
}
