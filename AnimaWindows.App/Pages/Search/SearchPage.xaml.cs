using AnimaWindows.App.Controls;
using AnimaWindows.App.Services;
using AnimaWindows.App.ViewModels;
using AnimaWindows.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AnimaWindows.App.Pages.Search;

public sealed partial class SearchPage : Page
{
    public SearchPage()
    {
        ViewModel = AppServices.Get<SearchViewModel>();
        InitializeComponent();
    }

    public SearchViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string query && !string.IsNullOrWhiteSpace(query))
        {
            _ = ViewModel.SearchAsync(query);
        }
    }

    private void OnResultInvoked(ItemsView sender, ItemsViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is Anime anime)
        {
            PosterTransition.PrepareFor(sender, anime);
            ViewModel.OpenAnime(anime);
        }
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query })
        {
            _ = ViewModel.SearchAsync(query);
        }
    }

    private void OnRemoveRecentClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string query })
        {
            _ = ViewModel.RemoveRecentAsync(query);
        }
    }
}
