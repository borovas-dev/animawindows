using AnimaWindows.App.Services;
using AnimaWindows.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AnimaWindows.App.Pages.Settings;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        ViewModel = AppServices.Get<SettingsViewModel>();
        InitializeComponent();
    }

    public SettingsViewModel ViewModel { get; }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.Start();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.Stop();
    }

    private void OnMoveProviderUp(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProviderRowViewModel row })
        {
            ViewModel.MoveProvider(row, -1);
        }
    }

    private void OnMoveProviderDown(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: ProviderRowViewModel row })
        {
            ViewModel.MoveProvider(row, 1);
        }
    }
}
