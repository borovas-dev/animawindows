using AnimaWindows.App.Services;
using AnimaWindows.App.Services.Notifications;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimaWindows.App.Pages.Downloads;

/// <summary>Offline downloads grouped per show.</summary>
public sealed partial class DownloadsPage : Page
{
    private bool _dialogOpen;

    public DownloadsPage()
    {
        ViewModel = AppServices.Get<DownloadsViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal DownloadsViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.ConfirmAsync = ConfirmAsync;
        ViewModel.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.Stop();
        ViewModel.ConfirmAsync = null;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel)
    {
        if (_dialogOpen || XamlRoot is null)
        {
            return false;
        }

        _dialogOpen = true;
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                PrimaryButtonText = confirmLabel,
                CloseButtonText = UiText.Get("Downloads_Cancel", "Cancel"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            // Through the shared service: WinUI throws when two ContentDialogs are open on one root (e.g. the Cloudflare check).
            return await AppServices.Get<IDialogService>().ShowAsync(dialog) == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }
}
