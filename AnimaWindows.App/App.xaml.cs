using AnimaWindows.App.Composition;
using AnimaWindows.App.Services;
using AnimaWindows.App.Services.Notifications;
using AnimaWindows.App.Shell;
using AnimaWindows.Core.Abstractions;
using AnimaWindows.Core.Downloads;
using AnimaWindows.Core.RemoteConfig;
using AnimaWindows.Core.Tracking;
using AnimaWindows.Core.Updates;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace AnimaWindows.App;

/// <summary>Application: builds the host (composition root), starts background services and handles activations.</summary>
public partial class App : Application
{
    private IHost? _host;
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteCrashLog(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => WriteCrashLog(e.ExceptionObject as Exception);
    }

    /// <summary>Appends the exception to crash.log in the app's local folder, so users can attach it to a bug report.</summary>
    private static void WriteCrashLog(Exception? error)
    {
        System.Diagnostics.Debug.WriteLine($"Unhandled: {error}");
        try
        {
            var path = Path.Combine(Windows.Storage.ApplicationData.Current.LocalFolder.Path, "crash.log");
            File.AppendAllText(path, $"[{DateTimeOffset.Now:O}] {AppVersion.Current}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Never throw from the crash handler.
        }
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddAnimaApp(queue);
        _host = builder.Build();
        AppServices.Initialize(_host.Services);

        try
        {
            await AppServices.Get<ISettingsStore>().LoadAsync();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Settings load failed: {e}");
        }

        _window = new MainWindow();
        _window.Activate();

        ActivationBridge.Subscribe(a => queue.TryEnqueue(() => HandleActivation(a)));
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());

        try
        {
            await _host.StartAsync();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Host start failed: {e}");
        }

        _ = Task.Run(StartBackgroundServicesAsync);
    }

    private static async Task StartBackgroundServicesAsync()
    {
        try
        {
            await AppServices.Get<DownloadManager>().StartAsync();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"DownloadManager start failed: {e}");
        }

        try
        {
            AppServices.Get<SyncScheduler>().Start();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"SyncScheduler start failed: {e}");
        }

        try
        {
            await AppServices.Get<IRemoteConfigSource>().RefreshAsync();
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Remote config refresh failed: {e}");
        }

        try
        {
            var settings = AppServices.Get<ISettingsStore>().Current;
            if (settings.CheckForUpdates)
            {
                await AppServices.Get<UpdateChecker>().CheckAsync(settings.IncludePrereleases);
            }
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Update check failed: {e}");
        }
    }

    private void HandleActivation(AppActivationArguments args)
    {
        switch (args.Kind)
        {
            case ExtendedActivationKind.Protocol when args.Data is IProtocolActivatedEventArgs protocol:
                var uri = protocol.Uri;
                if (string.Equals(uri.Scheme, SyncRepository.RedirectScheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(uri.Host, "auth", StringComparison.OrdinalIgnoreCase))
                {
                    _ = AppServices.Get<ISyncRepository>().HandleRedirectAsync(uri.AbsoluteUri);
                }

                break;
            case ExtendedActivationKind.AppNotification when args.Data is AppNotificationActivatedEventArgs toast:
                AppServices.Get<DownloadNotifier>().HandleActivation(toast.Arguments);
                break;
        }

        _window?.BringToFront();
    }
}
