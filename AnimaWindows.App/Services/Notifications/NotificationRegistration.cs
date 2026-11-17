using Microsoft.Windows.AppNotifications;

namespace AnimaWindows.App.Services.Notifications;

/// <summary>One-time <c>AppNotificationManager</c> registration; routes toast activations to <see cref="DownloadNotifier"/>.</summary>
internal static class NotificationRegistration
{
    private static int _registered;

    /// <summary>False when registration failed (e.g. no package identity); toasts are then silently skipped.</summary>
    public static bool IsRegistered { get; private set; }

    public static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        try
        {
            var manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();
            IsRegistered = true;
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"AppNotificationManager.Register failed: {e.Message}");
        }
    }

    private static void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        try
        {
            AppServices.Get<DownloadNotifier>().HandleActivation(args.Arguments);
        }
        catch (Exception e)
        {
            System.Diagnostics.Debug.WriteLine($"Toast activation failed: {e.Message}");
        }
    }
}
