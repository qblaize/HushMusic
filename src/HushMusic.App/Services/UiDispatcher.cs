using Microsoft.UI.Dispatching;

namespace HushMusic.App.Services;

/// <summary>Marshals work onto the UI thread. Core/Playback events arrive on background threads.</summary>
public interface IUiDispatcher
{
    bool HasThreadAccess { get; }

    /// <summary>Runs <paramref name="action"/> on the UI thread (inline if already there).</summary>
    void Run(Action action);

    Task RunAsync(Action action);
}

public sealed class UiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;

    public void Run(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }

    public Task RunAsync(Action action)
    {
        if (queue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queued = queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });

        if (!queued)
        {
            tcs.SetCanceled();
        }

        return tcs.Task;
    }
}
