using AnimaWindows.App.Controls;
using AnimaWindows.App.Services;
using AnimaWindows.App.ViewModels;
using AnimaWindows.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AnimaWindows.App.Pages.Home;

public sealed partial class HomePage : Page
{
    private CancellationTokenSource? _cts;

    public HomePage()
    {
        ViewModel = AppServices.Get<HomeViewModel>();
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; }

    public IReadOnlyList<int> SkeletonItems { get; } = (int[])[1, 2, 3, 4, 5, 6, 7, 8];

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        _cts = new CancellationTokenSource();
        _ = ViewModel.RunAsync(_cts.Token);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cts?.Cancel();
        _cts = null;
    }

    private void OnSearchClick(object sender, RoutedEventArgs e) => ViewModel.GoToSearch();

    private void OnContinueInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is ContinueCard card)
        {
            PosterTransition.PrepareFor(sender, card);
            ViewModel.OpenAnime(card.Anime);
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: Anime anime } button)
        {
            PosterTransition.Prepare(button);
            ViewModel.OpenAnime(anime);
        }
    }

    private void OnRemoveHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ContinueCard card })
        {
            _ = ViewModel.RemoveFromHistoryAsync(card);
        }
    }
}
