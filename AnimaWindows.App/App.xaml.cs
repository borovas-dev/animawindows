using Microsoft.UI.Xaml;

namespace AnimaWindows.App;

/// <summary>Application entry point. Composition root (DI via Microsoft.Extensions.Hosting) is added by the UI work package.</summary>
public partial class App : Application
{
    private Window? _window;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }
}
