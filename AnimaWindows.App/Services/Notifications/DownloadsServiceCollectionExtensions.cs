using AnimaWindows.App.Controls.Tracking;
using AnimaWindows.App.Pages.Downloads;
using AnimaWindows.App.Pages.Health;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AnimaWindows.App.Services.Notifications;

public static class DownloadsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the downloads UI: toast registration (<c>AppNotificationManager.Default.Register()</c>), the
    /// <see cref="DownloadNotifier"/> (also a hosted service) and the view models of the downloads, health and tracking UI.
    /// </summary>
    public static IServiceCollection AddDownloadsUi(this IServiceCollection services)
    {
        NotificationRegistration.Register();
        services.AddSingleton<DownloadNotifier>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<DownloadNotifier>());
        services.AddTransient<DownloadsViewModel>();
        services.AddTransient<HealthViewModel>();
        services.AddTransient<TrackingViewModel>();
        return services;
    }
}
