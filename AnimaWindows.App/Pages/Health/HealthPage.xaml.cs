using AnimaWindows.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimaWindows.App.Pages.Health;

/// <summary>Live per-provider health check (search, episodes, streams).</summary>
public sealed partial class HealthPage : Page
{
    public HealthPage()
    {
        ViewModel = AppServices.Get<HealthViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal HealthViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ViewModel.RunCommand.CanExecute(null))
        {
            ViewModel.RunCommand.Execute(null);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Cancel();
}
