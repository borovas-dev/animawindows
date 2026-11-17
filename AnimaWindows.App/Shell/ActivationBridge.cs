using Microsoft.Windows.AppLifecycle;

namespace AnimaWindows.App.Shell;

/// <summary>
/// Carries activations from <see cref="Program"/> (redirected from a second instance, on a background thread) to the app.
/// Activations that arrive before the app subscribes are queued.
/// </summary>
public static class ActivationBridge
{
    private static readonly object Gate = new();
    private static readonly Queue<AppActivationArguments> Pending = new();
    private static Action<AppActivationArguments>? _handler;

    public static void Raise(AppActivationArguments args)
    {
        Action<AppActivationArguments>? handler;
        lock (Gate)
        {
            handler = _handler;
            if (handler is null)
            {
                Pending.Enqueue(args);
            }
        }

        handler?.Invoke(args);
    }

    public static void Subscribe(Action<AppActivationArguments> handler)
    {
        List<AppActivationArguments> queued;
        lock (Gate)
        {
            _handler = handler;
            queued = [.. Pending];
            Pending.Clear();
        }

        foreach (var args in queued)
        {
            handler(args);
        }
    }
}
