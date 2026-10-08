using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// Keeps the music going when a finite queue ends (<see cref="IQueueAutoplay"/>). When the last track of an album,
/// playlist or list of songs starts and repeat is off, it asks for a radio seeded by that track (the watch endpoint with
/// <c>radio</c>, as "Start radio" does) and appends the songs that aren't queued yet. Near the end of those it loads the
/// radio's next page, the way <see cref="QueueAutoExtender"/> tops up radio queues.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Fetching starts when the last track starts, not when it ends, so the next song is ready (and pre-resolved by the
/// player) before the queue runs out.</item>
/// <item>The radio's continuation stays here instead of on the queue: clearing Up next or turning the setting off stops
/// it, and a restored session doesn't pick it up again.</item>
/// <item>Removing everything after the current song means "stop after this one": autoplay stays quiet for that queue
/// until songs are added to it again.</item>
/// <item>Turning the setting off removes the suggestions that haven't played yet.</item>
/// </list>
/// </remarks>
public sealed class QueueAutoplay(
    IQueueService queue,
    IPlayer player,
    IWatchApi watchApi,
    ISettingsService settings,
    ILogger<QueueAutoplay> logger) : IQueueAutoplay, IHostedService, IDisposable
{
    private const long RetryDelayMs = 30_000;

    // Radio pages in a row that bring nothing new (everything on them is queued already) before starting over from the
    // queue's new last track instead.
    private const int MaxEmptyPages = 3;

    private static readonly IReadOnlySet<Guid> NoItems = new HashSet<Guid>();

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stopping = new();

    // All guarded by _gate. _generation changes whenever the queue is replaced or cleared; results for an older one are dropped.
    private IReadOnlySet<Guid> _suggested = NoItems;
    private Track? _seed;
    private string? _continuation;
    private int _generation;
    private bool _suppressed;
    private Guid? _startedItem;
    private int _upcoming;
    private bool _enabled;
    private bool _inFlight;
    private int _emptyPages;
    private object? _failedKey;
    private long _retryAfterTicks;
    private int _ownChanges;

    public event EventHandler? Changed;

    public IReadOnlySet<Guid> SuggestedItemIds
    {
        get
        {
            lock (_gate)
            {
                return _suggested;
            }
        }
    }

    public Track? Seed
    {
        get
        {
            lock (_gate)
            {
                return _seed;
            }
        }
    }

    private bool IsEnabled => settings.Current.AutoplayWhenQueueEnds;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _enabled = IsEnabled;
            _upcoming = Upcoming(queue.GetSnapshot());
        }

        queue.Changed += OnQueueChanged;
        queue.CurrentChanged += OnCurrentChanged;
        player.TrackStarted += OnTrackStarted;
        settings.Changed += OnSettingsChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Changed -= OnQueueChanged;
        queue.CurrentChanged -= OnCurrentChanged;
        player.TrackStarted -= OnTrackStarted;
        settings.Changed -= OnSettingsChanged;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private static int Upcoming(QueueSnapshot snapshot) =>
        snapshot.CurrentIndex < 0 ? snapshot.Items.Count : snapshot.Items.Count - 1 - snapshot.CurrentIndex;

    // "Play" on one song (or "Start radio") loads it alone and fills Up next right after; that fill, not autoplay, follows.
    private static bool IsAwaitingUpNext(QueueSnapshot snapshot) =>
        snapshot.Source is { Kind: QueueSourceKind.Radio or QueueSourceKind.UpNext } source
        && snapshot.Items.Count == 1
        && string.Equals(source.Id, snapshot.Items[0].Track.VideoId, StringComparison.Ordinal);

    private void OnQueueChanged(object? sender, QueueChangedEventArgs e)
    {
        var snapshot = queue.GetSnapshot();
        var upcoming = Upcoming(snapshot);
        var changed = false;
        lock (_gate)
        {
            switch (e.Kind)
            {
                case QueueChangeKind.Reset or QueueChangeKind.Cleared:
                    _generation++;
                    changed = _suggested.Count > 0;
                    _suggested = NoItems;
                    _seed = null;
                    _continuation = null;
                    _suppressed = false;
                    _emptyPages = 0;
                    break;

                case QueueChangeKind.Removed:
                    if (_ownChanges == 0 && _upcoming > 0 && upcoming == 0)
                    {
                        _suppressed = true;
                        _continuation = null;
                    }

                    changed = PruneNoLock(snapshot);
                    break;

                case QueueChangeKind.Added when _ownChanges == 0 && upcoming > _upcoming:
                    _suppressed = false;
                    break;
            }

            _upcoming = upcoming;
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        // Removing songs never pulls in new ones at once (the user may be clearing Up next); the next song change does.
        if (e.Kind != QueueChangeKind.Removed)
        {
            TryExtend();
        }

        TrySeed();
    }

    private void OnCurrentChanged(object? sender, QueueCurrentChangedEventArgs e)
    {
        lock (_gate)
        {
            _upcoming = Upcoming(queue.GetSnapshot());
        }

        TryExtend();
    }

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            _startedItem = e.Item?.Id;
        }

        TrySeed();
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var enabled = IsEnabled;
        bool turnedOff;
        lock (_gate)
        {
            turnedOff = _enabled && !enabled;
            _enabled = enabled;
            if (turnedOff)
            {
                _continuation = null;
            }
        }

        if (turnedOff)
        {
            RemoveUpcomingSuggestions();
        }
        else if (enabled)
        {
            TrySeed();
        }
    }

    private void TrySeed()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        var snapshot = queue.GetSnapshot();
        QueueItem seed;
        int generation;
        lock (_gate)
        {
            if (_inFlight || !CanSeedNoLock(snapshot, out seed) || IsBackingOffNoLock(seed.Id))
            {
                return;
            }

            _inFlight = true;
            generation = _generation;
        }

        _ = SeedAsync(seed, generation);
    }

    private bool CanSeedNoLock(QueueSnapshot snapshot, out QueueItem seed)
    {
        seed = null!;
        if (!IsEnabled || _suppressed || _continuation is not null
            || snapshot.Continuation is not null // a radio, mix or long playlist that extends itself
            || snapshot.RepeatMode != RepeatMode.Off
            || snapshot.Source?.Kind == QueueSourceKind.LiveRadio
            || IsAwaitingUpNext(snapshot))
        {
            return false;
        }

        var index = snapshot.CurrentIndex;
        if (index < 0 || index != snapshot.Items.Count - 1)
        {
            return false;
        }

        var current = snapshot.Items[index];
        if (current.Track.IsLiveRadio || current.Id != _startedItem)
        {
            return false;
        }

        seed = current;
        return true;
    }

    private async Task SeedAsync(QueueItem seed, int generation)
    {
        var released = false;
        try
        {
            var watch = await watchApi.GetWatchPlaylistAsync(seed.Track.VideoId, playlistId: null, radio: true, shuffle: false, _stopping.Token)
                .ConfigureAwait(false);

            var snapshot = queue.GetSnapshot();
            lock (_gate)
            {
                // Replaced, cleared, added to, repeat or the setting changed while the request ran: not wanted any more.
                if (generation != _generation || !CanSeedNoLock(snapshot, out var current) || current.Id != seed.Id)
                {
                    logger.LogDebug("The queue changed while songs like {VideoId} were loading; dropping them", seed.Track.VideoId);
                    return;
                }

                // Released before appending: the append raises Changed, which loads the next page if this one was short.
                _inFlight = false;
                released = true;
                _continuation = watch.Continuation;
                _seed = seed.Track;
                _emptyPages = 0;
            }

            var added = Append(watch.Tracks, snapshot, generation);
            logger.LogInformation("The queue ended; added {Count} songs like {VideoId}", added, seed.Track.VideoId);
            if (added == 0)
            {
                TryExtend();
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load songs to play after the queue ends");
            lock (_gate)
            {
                _failedKey = seed.Id;
                _retryAfterTicks = Environment.TickCount64 + RetryDelayMs;
            }
        }
        finally
        {
            if (!released)
            {
                lock (_gate)
                {
                    _inFlight = false;
                }
            }
        }
    }

    private void TryExtend()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        var snapshot = queue.GetSnapshot();
        string token;
        int generation;
        lock (_gate)
        {
            if (_inFlight || _continuation is not { } continuation || !CanExtendNoLock(snapshot) || IsBackingOffNoLock(continuation))
            {
                return;
            }

            var remaining = snapshot.Items.Count - 1 - snapshot.CurrentIndex;
            if (snapshot.CurrentIndex < 0 || remaining > QueueAutoExtender.RemainingThreshold)
            {
                return;
            }

            _inFlight = true;
            token = continuation;
            generation = _generation;
        }

        _ = ExtendAsync(token, generation);
    }

    private bool CanExtendNoLock(QueueSnapshot snapshot) =>
        IsEnabled && !_suppressed && snapshot.Continuation is null && snapshot.RepeatMode == RepeatMode.Off;

    private async Task ExtendAsync(string continuation, int generation)
    {
        var released = false;
        try
        {
            var page = await watchApi.GetWatchPlaylistContinuationAsync(continuation, _stopping.Token).ConfigureAwait(false);
            var snapshot = queue.GetSnapshot();
            lock (_gate)
            {
                if (generation != _generation || _continuation != continuation || !CanExtendNoLock(snapshot))
                {
                    return;
                }

                _inFlight = false;
                released = true;
                _continuation = page.Continuation;
            }

            var added = Append(page.Items, snapshot, generation);
            logger.LogDebug("Added {Count} more songs after the end of the queue", added);
            if (added > 0)
            {
                lock (_gate)
                {
                    _emptyPages = 0;
                }

                return;
            }

            bool more;
            lock (_gate)
            {
                more = generation == _generation && ++_emptyPages < MaxEmptyPages;
                if (!more && generation == _generation)
                {
                    // This radio keeps repeating what is queued: let the queue's new last track seed a fresh one.
                    _continuation = null;
                }
            }

            if (more)
            {
                TryExtend();
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load more songs after the end of the queue");
            lock (_gate)
            {
                _failedKey = continuation;
                _retryAfterTicks = Environment.TickCount64 + RetryDelayMs;
            }
        }
        finally
        {
            if (!released)
            {
                lock (_gate)
                {
                    _inFlight = false;
                }
            }
        }
    }

    /// <summary>Appends the tracks that aren't queued yet and marks them as suggestions. Returns how many were added.</summary>
    private int Append(IReadOnlyList<Track> tracks, QueueSnapshot before, int generation)
    {
        var queued = new HashSet<string>(before.Items.Select(i => i.Track.VideoId), StringComparer.Ordinal);
        List<Track> fresh = [.. tracks.Where(t => t.IsAvailable && !t.IsLiveRadio && queued.Add(t.VideoId))];
        if (fresh.Count == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            _ownChanges++;
        }

        try
        {
            queue.Enqueue(fresh);
        }
        finally
        {
            lock (_gate)
            {
                _ownChanges--;
            }
        }

        // The queue wraps each track in a new item; find those items by the track instances that went in.
        var mine = new HashSet<Track>(fresh, ReferenceEqualityComparer.Instance);
        var after = queue.GetSnapshot();
        var ids = after.Items.Where(i => mine.Contains(i.Track)).Select(i => i.Id).ToList();
        lock (_gate)
        {
            if (generation != _generation || ids.Count == 0)
            {
                return fresh.Count;
            }

            var suggested = new HashSet<Guid>(_suggested);
            suggested.UnionWith(ids);
            _suggested = suggested;
        }

        Changed?.Invoke(this, EventArgs.Empty);
        ResumeIfEnded(after, ids[0]);
        return fresh.Count;
    }

    // The suggestions came in after the last song had already finished (a slow network): carry on with them.
    private void ResumeIfEnded(QueueSnapshot snapshot, Guid firstAdded)
    {
        if (player.Status != PlaybackStatus.Ended)
        {
            return;
        }

        var index = IndexOf(snapshot.Items, firstAdded);
        if (index >= 0 && index == snapshot.CurrentIndex + 1)
        {
            _ = ResumeAsync(index);
        }
    }

    private async Task ResumeAsync(int index)
    {
        try
        {
            await player.PlayQueueIndexAsync(index, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not continue with the songs added after the end of the queue");
        }
    }

    private void RemoveUpcomingSuggestions()
    {
        var snapshot = queue.GetSnapshot();
        IReadOnlySet<Guid> suggested;
        lock (_gate)
        {
            suggested = _suggested;
            _ownChanges++;
        }

        try
        {
            // Last first; each index is looked up again because the queue may change under us.
            var upcoming = snapshot.Items.Skip(snapshot.CurrentIndex + 1).Where(i => suggested.Contains(i.Id)).Select(i => i.Id).Reverse().ToList();
            foreach (var id in upcoming)
            {
                var index = IndexOf(queue.Items, id);
                if (index > queue.CurrentIndex)
                {
                    queue.RemoveAt(index);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _ownChanges--;
            }
        }
    }

    // Forgets suggestions that are no longer in the queue. Returns true when there were any.
    private bool PruneNoLock(QueueSnapshot snapshot)
    {
        if (_suggested.Count == 0)
        {
            return false;
        }

        var present = new HashSet<Guid>(snapshot.Items.Select(i => i.Id));
        if (_suggested.All(present.Contains))
        {
            return false;
        }

        _suggested = _suggested.Where(present.Contains).ToHashSet();
        return true;
    }

    private bool IsBackingOffNoLock(object key) => Equals(_failedKey, key) && Environment.TickCount64 < _retryAfterTicks;

    private static int IndexOf(IReadOnlyList<QueueItem> items, Guid id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }
}
