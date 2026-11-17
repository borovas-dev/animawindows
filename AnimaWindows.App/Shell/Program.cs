using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace AnimaWindows.App.Shell;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN): a second launch (for example the animawindows://auth/... protocol)
/// is redirected to the running instance instead of opening another window.
/// </summary>
public static class Program
{
    private const string InstanceKey = "animawindows-main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (IsRedirected())
        {
            return 0;
        }

        Application.Start(_ =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Registers this process as the main instance, or redirects to it. True = this process must exit.</summary>
    private static bool IsRedirected()
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent)
        {
            main.Activated += (_, e) => ActivationBridge.Raise(e);
            return false;
        }

        var done = new ManualResetEventSlim(false);
        Task.Run(() =>
        {
            try
            {
                main.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            catch (Exception)
            {
                // The main instance went away; nothing more to do.
            }
            finally
            {
                done.Set();
            }
        });
        done.Wait(TimeSpan.FromSeconds(10));
        return true;
    }
}
