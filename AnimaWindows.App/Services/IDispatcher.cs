using Microsoft.UI.Dispatching;

namespace AnimaWindows.App.Services;

/// <summary>Marshals work onto the UI thread. Core services raise events on thread-pool threads.</summary>
public interface IDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Fire-and-forget; returns false when the queue is shutting down.</summary>
    bool TryEnqueue(Action action);

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline when already there).</summary>
    Task RunAsync(Action action);

    /// <summary>Runs an async function on the UI thread and returns its result.</summary>
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
}

public sealed class DispatcherQueueDispatcher : IDispatcher
{
    private readonly DispatcherQueue _queue;

    public DispatcherQueueDispatcher(DispatcherQueue queue) => _queue = queue;

    public bool HasThreadAccess => _queue.HasThreadAccess;

    public bool TryEnqueue(Action action) => _queue.TryEnqueue(() => action());

    public Task RunAsync(Action action)
    {
        if (_queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource();
        var queued = _queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        if (!queued)
        {
            tcs.TrySetException(new InvalidOperationException("The dispatcher queue is not accepting work."));
        }

        return tcs.Task;
    }

    public Task<T> InvokeAsync<T>(Func<Task<T>> action)
    {
        if (_queue.HasThreadAccess)
        {
            return action();
        }

        var tcs = new TaskCompletionSource<T>();
        var queued = _queue.TryEnqueue(async () =>
        {
            try
            {
                tcs.SetResult(await action());
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        if (!queued)
        {
            tcs.TrySetException(new InvalidOperationException("The dispatcher queue is not accepting work."));
        }

        return tcs.Task;
    }
}
