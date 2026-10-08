using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features;

/// <summary>
/// Keeps radio and "up next" queues endless: when only a few items are left after the current one
/// and the queue has a continuation token, fetches the next page and appends it.
/// Reference example of a feature: it only listens to <see cref="IQueueService"/> events and reshapes the queue.
/// </summary>
public sealed class QueueAutoExtender(
    IQueueService queue,
    IWatchApi watchApi,
    ILogger<QueueAutoExtender> logger) : IHostedService, IDisposable
{
    /// <summary>Extend when this many items or fewer remain after the current one.</summary>
    public const int RemainingThreshold = 3;

    private const long RetryDelayMs = 30_000;

    private readonly CancellationTokenSource _stopping = new();
    private int _inFlight;
    private string? _failedContinuation;
    private long _retryAfterTicks;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        queue.Changed += OnQueueChanged;
        queue.CurrentChanged += OnCurrentChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Changed -= OnQueueChanged;
        queue.CurrentChanged -= OnCurrentChanged;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private void OnQueueChanged(object? sender, QueueChangedEventArgs e) => TryExtend();

    private void OnCurrentChanged(object? sender, QueueCurrentChangedEventArgs e) => TryExtend();

    private void TryExtend()
    {
        var continuation = queue.Continuation;
        if (continuation is null || _stopping.IsCancellationRequested)
        {
            return;
        }

        var remaining = queue.Items.Count - 1 - queue.CurrentIndex;
        if (remaining > RemainingThreshold)
        {
            return;
        }

        // Don't hammer a token that just failed; any later queue event retries it after the delay.
        if (continuation == Volatile.Read(ref _failedContinuation) && Environment.TickCount64 < Volatile.Read(ref _retryAfterTicks))
        {
            return;
        }

        // One request at a time; the append raises Changed, which re-checks and chains the next page if needed.
        if (Interlocked.CompareExchange(ref _inFlight, 1, 0) != 0)
        {
            return;
        }

        _ = ExtendAsync(continuation);
    }

    private async Task ExtendAsync(string continuation)
    {
        var released = false;
        try
        {
            var page = await watchApi.GetWatchPlaylistContinuationAsync(continuation, _stopping.Token).ConfigureAwait(false);

            // The queue may have been replaced while the request was running.
            if (queue.Continuation != continuation)
            {
                return;
            }

            // Release before appending: the append raises Changed, which chains the next page if still short.
            released = true;
            Volatile.Write(ref _inFlight, 0);
            queue.AppendContinuation([.. page.Items.Where(t => t.IsAvailable)], page.Continuation);
            logger.LogDebug("Extended the queue with {Count} tracks", page.Items.Count);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not extend the queue");
            Volatile.Write(ref _retryAfterTicks, Environment.TickCount64 + RetryDelayMs);
            Volatile.Write(ref _failedContinuation, continuation);
        }
        finally
        {
            if (!released)
            {
                Volatile.Write(ref _inFlight, 0);
            }
        }
    }
}
