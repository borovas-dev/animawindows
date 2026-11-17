using AnimaWindows.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimaWindows.App.Controls.Tracking;

/// <summary>AniList / MyAnimeList connection settings; hosted by Settings > Advanced.</summary>
public sealed partial class TrackingSection : UserControl
{
    public TrackingSection()
    {
        ViewModel = AppServices.Get<TrackingViewModel>();
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    internal TrackingViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs e) => ViewModel.Start();

    private void OnUnloaded(object sender, RoutedEventArgs e) => ViewModel.Stop();
}
