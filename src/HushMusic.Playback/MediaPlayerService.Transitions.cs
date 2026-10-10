using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;

namespace HushMusic.Playback;

// Preloading the next item on the spare player, crossfades and gapless starts (see the remarks on the main part).
public sealed partial class MediaPlayerService
{
    // Test instances only: HUSHMUSIC_TEST_SEEK_BEFORE_END=<seconds> jumps every track to that long before its end once
    // its audio starts, so transitions can be checked without sitting through whole songs. Unset, it does nothing.
    private readonly TimeSpan? _testSeekBeforeEnd = ReadTestSeekBeforeEnd();

    private static TimeSpan? ReadTestSeekBeforeEnd() =>
        double.TryParse(Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_SEEK_BEFORE_END"), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : null;

    // How long the item took to be heard after the previous one ended by itself, or after a preloaded item took over.
    private void LogStartNoLock(QueueItem item)
    {
        var now = Environment.TickCount64;
        if (_takeoverAtMs > 0)
        {
            _logger.LogDebug(
                "{VideoId} playing on player {Deck} {Elapsed} ms after taking over{Gap}",
                item.Track.VideoId,
                _active.Name,
                now - _takeoverAtMs,
                _endedAtMs > 0 ? $" ({now - _endedAtMs} ms after the previous track ended)" : string.Empty);
        }
        else if (_endedAtMs > 0)
        {
            _logger.LogDebug("{VideoId} playing {Elapsed} ms after the previous track ended (not preloaded)", item.Track.VideoId, now - _endedAtMs);
        }

        _takeoverAtMs = 0;
        _endedAtMs = 0;
    }

    private void TestSeekBeforeEnd(long loadId, TimeSpan before)
    {
        lock (_gate)
        {
            var duration = DurationNoLock();
            if (loadId != _loadId || _disposed || _source is null || !_opened || duration <= before + before)
            {
                return;
            }

            try
            {
                _active.Player.PlaybackSession.Position = duration - before;
                _logger.LogInformation("Test: jumped {VideoId} to {Position} ({Before} before the end)", _item?.Track.VideoId, duration - before, before);
            }
            catch (COMException ex)
            {
                _logger.LogDebug(ex, "Test: could not jump to the end");
            }
        }
    }

    private void ReviewPreload()
    {
        var next = _queue.PeekNext();
        CancellationTokenSource? dropped = null;
        lock (_gate)
        {
            if (_preload is { } preload && preload.Item.Id != next?.Id && preload.Item.Id != _item?.Id)
            {
                dropped = DropPreloadNoLock("it no longer comes next");
            }
        }

        dropped?.Cancel();
    }

    // Called on every position tick while playing, and by the transition timer: prepares the next item and starts the
    // crossfade on time (see TransitionPlanner).
    private void EvaluateTransition()
    {
        var next = _queue.PeekNext();
        var repeatOne = _queue.RepeatMode == RepeatMode.One;
        CancellationTokenSource? dropped = null;
        Preload? started = null;
        QueueItem? fadeInto = null;
        var fade = TimeSpan.Zero;
        var loadId = 0L;
        lock (_gate)
        {
            if (_disposed || _status != PlaybackStatus.Playing || _item is not { } item || _source is null || !_opened || _ended || _crossfadeStarting)
            {
                return;
            }

            if (_preload is { } stale && (stale.Item.Id != next?.Id || IsExpiringNoLock(stale)))
            {
                var expiring = stale.Item.Id == next?.Id;
                dropped = DropPreloadNoLock(expiring ? "its link is about to expire" : "it no longer comes next");
                if (expiring)
                {
                    _resolver.Invalidate(stale.Item.Track.VideoId);
                }
            }

            var position = PositionNoLock();
            var plan = TransitionPlanner.Plan(new TransitionContext
            {
                Crossfade = _crossfade,
                Duration = DurationNoLock(),
                HasNext = next is not null,
                NextIsCurrent = next?.Id == item.Id,
                RepeatOne = repeatOne,
                CurrentIsLive = item.Track.IsLiveRadio,
                NextIsLive = next?.Track.IsLiveRadio == true,
                PauseAtEnd = _pauseAtEnd,
                SoughtIntoFade = _soughtIntoFade,
                NextDuration = _preload?.Stream?.Duration ?? next?.Track.Duration,
            });
            LogPlanNoLock(item, next, plan);

            var preload = _preload;
            var step = TransitionPlanner.Step(
                plan,
                position,
                preloadReady: preload is not null && IsReadyNoLock(preload),
                preloadPending: preload is not null && !preload.Opened,
                spareFree: _fadeOut is null);
            if (step.Preload && preload is null && next is not null && _preloadFailedFor != next.Id)
            {
                started = StartPreloadNoLock(next, plan, position);
            }

            switch (step.Action)
            {
                case TransitionAction.ArmTimer:
                    _transitionTimer.Change(step.Delay, Timeout.InfiniteTimeSpan);
                    break;
                case TransitionAction.NotReady when !_notReadyLogged:
                    _notReadyLogged = true;
                    _logger.LogInformation(
                        "Crossfade into {Next} is due at {Position} but it isn't open yet ({State}); it starts when it opens, or when {VideoId} ends",
                        next?.Track.VideoId,
                        position,
                        preload is null ? "not preloaded" : "still opening",
                        item.Track.VideoId);
                    break;
                case TransitionAction.StartCrossfade:
                    _crossfadeStarting = true;
                    loadId = _loadId;
                    fadeInto = preload!.Item;
                    fade = step.Fade;
                    break;
            }
        }

        dropped?.Cancel();
        if (started is not null)
        {
            _ = PreloadAsync(started);
        }

        if (fadeInto is not null)
        {
            StartCrossfade(loadId, fadeInto, fade);
        }
    }

    private void LogPlanNoLock(QueueItem item, QueueItem? next, in TransitionPlan plan)
    {
        var key = $"{item.Id}|{next?.Id}|{plan.Kind}|{plan.Reason}|{plan.Fade.Ticks}|{plan.Duration.Ticks}";
        if (key == _loggedPlan)
        {
            return;
        }

        _loggedPlan = key;
        if (plan.Kind == TransitionKind.Crossfade)
        {
            _logger.LogDebug(
                "Transition {VideoId} -> {Next}: crossfade {Fade} s at {FadeAt} of {Duration}, preload at {PreloadAt}",
                item.Track.VideoId,
                next?.Track.VideoId,
                plan.Fade.TotalSeconds,
                plan.FadeAt,
                plan.Duration,
                plan.PreloadAt);
        }
        else
        {
            _logger.LogDebug(
                "Transition {VideoId} -> {Next}: {Kind} ({Reason}){Preload}",
                item.Track.VideoId,
                next?.Track.VideoId,
                plan.Kind == TransitionKind.Gapless ? "gapless" : "no preload",
                plan.Reason,
                plan.Kind == TransitionKind.Gapless ? $", preload at {plan.PreloadAt}" : string.Empty);
        }
    }

    private Preload StartPreloadNoLock(QueueItem next, in TransitionPlan plan, TimeSpan position)
    {
        var preload = new Preload(next, _spare, ++_ids);
        _preload = preload;

        // Silent until it takes over (it is paused anyway); the takeover sets its crossfade gain.
        _mixer.SetMixGain(_spare.Index, 0, TimeSpan.Zero, RampCurve.Linear);
        StartPreloadLoudnessNoLock(preload);
        _logger.LogDebug(
            "Preloading {VideoId} on player {Deck} at {Position} of {Duration} ({Kind})",
            next.Track.VideoId,
            _spare.Name,
            position,
            plan.Duration,
            plan.Kind == TransitionKind.Crossfade ? $"crossfade at {plan.FadeAt}" : "gapless");
        return preload;
    }

    private async Task PreloadAsync(Preload preload)
    {
        var token = preload.Cts.Token;
        ResolvedStream stream;
        try
        {
            stream = await _resolver.ResolveAsync(preload.Item.Track, token).ConfigureAwait(false);
            await WarmUpHostAsync(stream.Url, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return; // dropped
        }
        catch (Exception ex)
        {
            GiveUpPreload(preload, ex.Message);
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_preload != preload || _disposed)
                {
                    return;
                }

                var source = MediaSource.CreateFromUri(stream.Url);
                source.CustomProperties[LoadIdKey] = preload.Id;
                preload.Stream = stream;
                preload.Source = source;
                EnsureOutputNoLock(preload.Deck);
                preload.Deck.Player.Source = source;
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            GiveUpPreload(preload, ex.Message);
        }
    }

    // The preloaded item can't be opened ahead: it is resolved and opened the normal way when its turn comes (with the
    // full retry ladder and error reporting there).
    private void GiveUpPreload(Preload preload, string error)
    {
        CancellationTokenSource? dropped = null;
        lock (_gate)
        {
            if (_preload != preload || _disposed)
            {
                return;
            }

            _preloadFailedFor = preload.Item.Id;
            dropped = DropPreloadNoLock("it could not be opened");
        }

        _logger.LogWarning("Could not preload {VideoId}: {Error}. It opens when its turn comes", preload.Item.Track.VideoId, error);
        dropped?.Cancel();
    }

    // The preload's own failure ladder: open once more (with a fresh link when the URL was refused), then give up.
    private void OnPreloadFailedNoLock(Preload preload, MediaPlayerFailedEventArgs args, int hresult)
    {
        var rejected = IsRejectedUrl(hresult);
        _logger.LogWarning(
            "Preloading {VideoId} failed: {Error} \"{Message}\" (0x{HResult:X8}); {Action}",
            preload.Item.Track.VideoId,
            args.Error,
            args.ErrorMessage,
            hresult,
            preload.Retried ? "giving up" : rejected ? "re-resolving once" : "reopening once");
        if (preload.Retried)
        {
            _ = Task.Run(() => GiveUpPreload(preload, args.Error.ToString()));
            return;
        }

        preload.Retried = true;
        var failedId = preload.Id;
        _ = Task.Run(() =>
        {
            lock (_gate)
            {
                if (_preload != preload || preload.Id != failedId || _disposed)
                {
                    return Task.CompletedTask;
                }

                if (preload.Source is { } source)
                {
                    StopDeckNoLock(preload.Deck, source);
                }

                preload.Source = null;
                preload.Stream = null;
                preload.Opened = false;
                preload.Id = ++_ids;
            }

            if (rejected)
            {
                _resolver.Invalidate(preload.Item.Track.VideoId);
            }

            return PreloadAsync(preload);
        });
    }

    private void StartCrossfade(long loadId, QueueItem target, TimeSpan fade)
    {
        QueueItem? next;
        using (MovingQueue())
        {
            next = _queue.MoveNext(userInitiated: false);
        }

        CancellationTokenSource? retired = null;
        QueueItem? instead = null;
        lock (_gate)
        {
            _crossfadeStarting = false;
            if (loadId != _loadId || _disposed || _item is not { } finished || next is null || next.Id == finished.Id)
            {
                return; // a skip got there first, or the queue changed under us without moving: the track ends normally
            }

            Post(() => RaiseTrackEvent(TrackCompleted, nameof(TrackCompleted), finished));
            if (next.Id == target.Id && _preload is { } preload && preload.Item.Id == target.Id && IsReadyNoLock(preload))
            {
                var position = PositionNoLock();
                var duration = DurationNoLock();
                var outgoing = _active;
                retired = TakeOverNoLock(preload, fade, _autoplay, TimeSpan.Zero, LoadKind.AutoAdvance);
                var incoming = _mixer.Levels(_active.Index);
                _logger.LogDebug(
                    "Crossfade {From} -> {To} over {Fade} ms from {Position} of {Duration}: player {Out} fades out, player {In} fades in (normalization {OutGain} -> {InGain}){Paused}",
                    finished.Track.VideoId,
                    target.Track.VideoId,
                    (long)fade.TotalMilliseconds,
                    position,
                    duration,
                    outgoing.Name,
                    _active.Name,
                    Format(_mixer.Levels(outgoing.Index).Loudness),
                    Format(incoming.Loudness),
                    _fadeOut is null ? " - paused meanwhile, switched without a fade" : string.Empty);
            }
            else
            {
                instead = next;
            }
        }

        retired?.Cancel();
        if (instead is not null)
        {
            _logger.LogDebug("The queue changed as the crossfade started; switching to {VideoId} directly", instead.Track.VideoId);
            _ = LoadAsync(instead, autoplay: true, TimeSpan.Zero, LoadKind.AutoAdvance, CancellationToken.None, onlyIfLoadId: loadId);
        }
    }

    /// <summary>
    /// The preloaded item becomes the current load on the spare player, which becomes the active one. The previous
    /// player fades out over <paramref name="fade"/> when it is audible (then stops), otherwise it stops at once.
    /// Returns the old load's token source; cancel it after leaving the lock.
    /// </summary>
    private CancellationTokenSource? TakeOverNoLock(Preload preload, TimeSpan fade, bool play, TimeSpan startAt, LoadKind kind)
    {
        var outgoing = _active;
        var outgoingSource = _source;
        var outgoingItem = _item;
        var audible = outgoingSource is not null && _opened && !_ended && _status is PlaybackStatus.Playing or PlaybackStatus.Buffering;
        var retired = _loadCts;
        var item = preload.Item;
        var itemChanged = _item?.Id != item.Id;

        _preload = null;
        _active = preload.Deck;
        _spare = outgoing;
        _loadCts = preload.Cts;
        _loadId = preload.Id;
        _source = preload.Source;
        _stream = preload.Stream;
        _opened = true;
        _item = item;
        _autoplay = play;
        _starting = play;
        _ended = false;
        _started = false;
        _startAt = startAt;
        _loadKind = kind;
        _reopened = false;
        _retried = false;
        _prefetchedFor = null;
        _livePolicy.Reset();
        ResetTransitionNoLock();
        if (kind == LoadKind.User)
        {
            _consecutiveFailures = 0;
            _endedAtMs = 0;
        }

        _takeoverAtMs = Environment.TickCount64;
        var fading = fade > TimeSpan.Zero && audible && play && outgoingItem is not null;
        if (outgoingSource is not null)
        {
            if (fading)
            {
                _fadeOut = new FadeOut(outgoing, outgoingSource, outgoingItem!, fade);
                _mixer.SetMixGain(outgoing.Index, 0, fade, RampCurve.EqualPowerOut);
                _fadeTimer.Change(fade, Timeout.InfiniteTimeSpan);
            }
            else
            {
                StopDeckNoLock(outgoing, outgoingSource);
            }
        }

        _mixer.SetMixGain(_active.Index, fading ? 0 : 1, TimeSpan.Zero, RampCurve.Linear);
        if (fading)
        {
            _mixer.SetMixGain(_active.Index, 1, fade, RampCurve.EqualPowerIn);
        }

        if (startAt > TimeSpan.Zero)
        {
            _active.Player.PlaybackSession.Position = startAt;
        }

        if (play)
        {
            _active.Player.Play();
            if (_status is not (PlaybackStatus.Playing or PlaybackStatus.Buffering))
            {
                SetStatusNoLock(PlaybackStatus.Loading);
            }
        }
        else
        {
            SetStatusNoLock(PlaybackStatus.Paused);
        }

        var duration = DurationNoLock();
        if (itemChanged)
        {
            Post(() =>
            {
                _radio.Unfollow(forget: true);
                _smtc.SetTrack(item.Track);
                RaiseTrackEvent(TrackChanged, nameof(TrackChanged), item);
            });
        }

        Post(() =>
        {
            PositionChanged?.Invoke(this, new PositionChangedEventArgs(startAt, duration));
            _smtc.SetTimeline(startAt, duration);
        });
        return retired;
    }

    /// <summary>Ends a running crossfade within <see cref="QuickFade"/>: the outgoing track fades out, the current one comes up to full.</summary>
    private void CutFadeNoLock(string why)
    {
        if (_fadeOut is not { } fading)
        {
            return;
        }

        var now = Environment.TickCount64;
        if (fading.EndsMs - now <= (long)QuickFade.TotalMilliseconds)
        {
            return; // ends within the quick fade anyway
        }

        fading.EndsMs = now + (long)QuickFade.TotalMilliseconds;
        _mixer.SetMixGain(fading.Deck.Index, 0, QuickFade, RampCurve.EqualPowerOut);
        _mixer.SetMixGain(_active.Index, 1, QuickFade, RampCurve.EqualPowerIn);
        _fadeTimer.Change(QuickFade, Timeout.InfiniteTimeSpan);
        _logger.LogDebug(
            "Crossfade cut short after {Elapsed} ms ({Why}): {VideoId} on player {Deck} fades out in {Quick} ms",
            now - fading.StartedMs,
            why,
            fading.Item.Track.VideoId,
            fading.Deck.Name,
            (long)QuickFade.TotalMilliseconds);
    }

    private void FinishFadeOut(bool force)
    {
        lock (_gate)
        {
            if (_fadeOut is not { } fading || _disposed)
            {
                return;
            }

            var left = fading.EndsMs - Environment.TickCount64;
            if (!force && left > 5)
            {
                _fadeTimer.Change(TimeSpan.FromMilliseconds(left), Timeout.InfiniteTimeSpan);
                return;
            }

            var outgoing = _mixer.Levels(fading.Deck.Index);
            var incoming = _mixer.Levels(_active.Index);
            StopFadeOutNoLock();
            _logger.LogDebug(
                "Crossfade done after {Elapsed} ms{Early}: stopped {VideoId} on player {Deck} at mix gain {OutGain}; player {In} at mix gain {InGain}",
                Environment.TickCount64 - fading.StartedMs,
                force ? " (the outgoing track ended first)" : string.Empty,
                fading.Item.Track.VideoId,
                fading.Deck.Name,
                Format(outgoing.Mix),
                _active.Name,
                Format(incoming.Mix));
        }
    }

    private void StopFadeOutNoLock()
    {
        if (_fadeOut is not { } fading)
        {
            return;
        }

        _fadeOut = null;
        _fadeTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        StopDeckNoLock(fading.Deck, fading.Source);
    }

    /// <summary>Lets go of the preload. Returns its token source; cancel it after leaving the lock.</summary>
    private CancellationTokenSource? DropPreloadNoLock(string why)
    {
        if (_preload is not { } preload)
        {
            return null;
        }

        _preload = null;
        if (preload.Source is { } source)
        {
            StopDeckNoLock(preload.Deck, source);
        }

        _logger.LogDebug("Dropped the preloaded {VideoId} on player {Deck} ({Why})", preload.Item.Track.VideoId, preload.Deck.Name, why);
        return preload.Cts;
    }

    private void StopDeckNoLock(Deck deck, MediaSource source)
    {
        try
        {
            deck.Player.Source = null;
            source.Dispose();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Closing a media source on player {Deck} failed", deck.Name);
        }
    }

    private bool IsReadyNoLock(Preload preload) => preload.Opened && preload.Source is not null && !IsExpiringNoLock(preload);

    // A preload can sit for a long time while paused; an expired link would fail on the first new range request.
    private static bool IsExpiringNoLock(Preload preload) =>
        preload.Stream is { } stream && stream.ExpiresAt != default && stream.ExpiresAt - DateTimeOffset.UtcNow <= RefreshBeforeExpiry;

    // Per-item transition state: a new item gets a fresh plan.
    private void ResetTransitionNoLock()
    {
        _crossfadeStarting = false;
        _soughtIntoFade = false;
        _notReadyLogged = false;
        _loggedPlan = null;
        _preloadFailedFor = null;
        _transitionTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    // The same for a preloaded item, on the spare player (silent until it takes over).
    private void StartPreloadLoudnessNoLock(Preload preload)
    {
        var output = preload.Deck.Index;
        if (!_normalize)
        {
            _mixer.SetLoudnessGain(output, 1, TimeSpan.Zero);
            return;
        }

        if (_normalizer.TryGetLoudness(preload.Item.Track.VideoId, out var loudnessDb))
        {
            var gain = PlaybackGain.ForLoudness(loudnessDb);
            _mixer.SetLoudnessGain(output, gain, TimeSpan.Zero);
            LogGain(preload.Item, loudnessDb, gain, $"preloaded on player {preload.Deck.Name}");
            return;
        }

        _mixer.SetLoudnessGain(output, 1, TimeSpan.Zero);
        _ = Task.Run(() => ApplyLoudnessWhenKnownAsync(preload.Item));
    }

    /// <summary>One of the two MediaPlayers, with the mixer output of the same index. Which one plays the current item changes at each takeover.</summary>
    private sealed class Deck : IDisposable
    {
        private readonly Windows.Foundation.TypedEventHandler<MediaPlayer, object> _opened;
        private readonly Windows.Foundation.TypedEventHandler<MediaPlayer, object> _ended;
        private readonly Windows.Foundation.TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> _failed;
        private readonly Windows.Foundation.TypedEventHandler<MediaPlaybackSession, object> _stateChanged;

        public Deck(MediaPlayerService owner, int index, double volume)
        {
            Index = index;
            Name = index == 0 ? "A" : "B";
            Player = new MediaPlayer
            {
                AudioCategory = MediaPlayerAudioCategory.Media,
                AutoPlay = false,
                Volume = volume,
            };
            _opened = (sender, _) => owner.OnMediaOpened(this, sender);
            _ended = (sender, _) => owner.OnMediaEnded(this, sender);
            _failed = (sender, args) => owner.OnMediaFailed(this, sender, args);
            _stateChanged = (session, _) => owner.OnPlaybackStateChanged(this, session);
            Player.MediaOpened += _opened;
            Player.MediaEnded += _ended;
            Player.MediaFailed += _failed;
            Player.PlaybackSession.PlaybackStateChanged += _stateChanged;
        }

        public int Index { get; }

        public string Name { get; }

        public MediaPlayer Player { get; }

        /// <summary>The audio output this player was last set to; null = the system default. Guarded by the owner's lock.</summary>
        public string? OutputId { get; set; }

        public void Dispose()
        {
            Player.MediaOpened -= _opened;
            Player.MediaEnded -= _ended;
            Player.MediaFailed -= _failed;
            Player.PlaybackSession.PlaybackStateChanged -= _stateChanged;
            Player.Dispose();
        }
    }

    /// <summary>The next queue item, resolved and opened (paused) on the spare player ahead of its turn.</summary>
    private sealed class Preload(QueueItem item, Deck deck, long id)
    {
        public QueueItem Item { get; } = item;

        public Deck Deck { get; } = deck;

        /// <summary>Tags this preload's media source; a new one after a retry, so the failed source's late events are ignored.</summary>
        public long Id { get; set; } = id;

        public CancellationTokenSource Cts { get; } = new();

        public long StartedMs { get; } = Environment.TickCount64;

        public MediaSource? Source { get; set; }

        public ResolvedStream? Stream { get; set; }

        public bool Opened { get; set; }

        public bool Retried { get; set; }
    }

    /// <summary>The previous track, still playing on the spare player while it fades out under the current one.</summary>
    private sealed class FadeOut(Deck deck, MediaSource source, QueueItem item, TimeSpan length)
    {
        public Deck Deck { get; } = deck;

        public MediaSource Source { get; } = source;

        public QueueItem Item { get; } = item;

        public long StartedMs { get; } = Environment.TickCount64;

        public long EndsMs { get; set; } = Environment.TickCount64 + (long)length.TotalMilliseconds;
    }
}
