using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features;

/// <summary>
/// Keeps radio and "up next" queues endless: when only a few items are left after the current one
/// and the queue has a continuation token, fetches the next page and appends it.
/// Reference example of a feature: it only listens to <see cref="IQueueService"/> events and reshapes the queue.
/// Radio and up-next pages leave out the songs <see cref="IAutoQueueFilter"/> rejects (often skipped, disliked, by a
/// blocked artist); the later pages of a playlist the user plays are appended whole. When the filter changes (a dislike,
/// a blocked artist), the songs after the current one in a radio or up-next queue that it now rejects are removed.
/// </summary>
public sealed class QueueAutoExtender(
    IQueueService queue,
    IWatchApi watchApi,
    ILogger<QueueAutoExtender> logger,
    IAutoQueueFilter? filter = null) : IHostedService, IDisposable
{
    /// <summary>Extend when this many items or fewer remain after the current one.</summary>
    public const int RemainingThreshold = 3;

    private const long RetryDelayMs = 30_000;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _pruneGate = new();
    private int _inFlight;
    private string? _failedContinuation;
    private long _retryAfterTicks;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        queue.Changed += OnQueueChanged;
        queue.CurrentChanged += OnCurrentChanged;
        if (filter is not null)
        {
            filter.Changed += OnFilterChanged;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Changed -= OnQueueChanged;
        queue.CurrentChanged -= OnCurrentChanged;
        if (filter is not null)
        {
            filter.Changed -= OnFilterChanged;
        }

        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private static bool IsAutomatic(QueueSource? source) => source?.Kind is QueueSourceKind.Radio or QueueSourceKind.UpNext;

    private void OnQueueChanged(object? sender, QueueChangedEventArgs e) => TryExtend();

    private void OnCurrentChanged(object? sender, QueueCurrentChangedEventArgs e) => TryExtend();

    private void OnFilterChanged(object? sender, EventArgs e) => RemoveLeftOut();

    /// <summary>
    /// Removes the songs after the current one that the filter rejects, when the whole queue came from a radio or up next
    /// (the songs the user added to it go too). The removals raise <see cref="IQueueService.Changed"/>, which tops the queue up.
    /// </summary>
    internal int RemoveLeftOut()
    {
        if (filter is null || _stopping.IsCancellationRequested)
        {
            return 0;
        }

        lock (_pruneGate)
        {
            var snapshot = queue.GetSnapshot();
            if (!IsAutomatic(snapshot.Source))
            {
                return 0;
            }

            var leftOut = snapshot.Items.Skip(snapshot.CurrentIndex + 1)
                .Where(i => !i.Track.IsLiveRadio && filter.IsLeftOut(i.Track))
                .Select(i => i.Id)
                .Reverse()
                .ToList();

            // Last first; each index is looked up again because the queue may change under us.
            var removed = 0;
            foreach (var id in leftOut)
            {
                var items = queue.Items;
                var index = -1;
                for (var i = items.Count - 1; i >= 0 && index < 0; i--)
                {
                    if (items[i].Id == id)
                    {
                        index = i;
                    }
                }

                if (index > queue.CurrentIndex)
                {
                    queue.RemoveAt(index);
                    removed++;
                }
            }

            if (removed > 0)
            {
                logger.LogDebug("Removed {Count} songs that are left out now from the queue", removed);
            }

            return removed;
        }
    }

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
            var available = page.Items.Where(t => t.IsAvailable);
            queue.AppendContinuation(IsAutomatic(queue.Source) ? filter.WithoutLeftOut(available) : [.. available], page.Continuation);
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
