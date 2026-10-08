using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features;

/// <summary>
/// Sleep timer. A timed one fires at <see cref="EndsAt"/>, fades the audio out over <see cref="FadeDuration"/> and pauses
/// (the user's volume is untouched: the player fades with an internal gain). It stays active during the fade, so
/// <see cref="Cancel"/> still stops it there. "End of track" pauses when the playing track ends by itself
/// (<see cref="IPlayer.PauseAtEndOfTrack"/>); skipping to another track keeps it armed for that track.
/// <see cref="Changed"/> is raised on the thread that caused the change (the caller, or a timer/player thread when it fires).
/// </summary>
public sealed class SleepTimer : ISleepTimer, IDisposable
{
    public static readonly TimeSpan FadeDuration = TimeSpan.FromSeconds(8);

    private readonly object _gate = new();
    private readonly IPlayer _player;
    private readonly TimeProvider _time;
    private readonly ILogger<SleepTimer> _logger;
    private ITimer? _timer;
    private CancellationTokenSource? _fade;
    private DateTimeOffset? _endsAt;
    private bool _endOfTrack;
    private long _generation;
    private bool _disposed;

    public SleepTimer(IPlayer player, TimeProvider time, ILogger<SleepTimer> logger)
    {
        _player = player;
        _time = time;
        _logger = logger;
        _player.TrackCompleted += OnTrackCompleted;
    }

    public event EventHandler? Changed;

    public bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _endsAt is not null || _endOfTrack;
            }
        }
    }

    public DateTimeOffset? EndsAt
    {
        get
        {
            lock (_gate)
            {
                return _endsAt;
            }
        }
    }

    public bool StopsAtEndOfTrack
    {
        get
        {
            lock (_gate)
            {
                return _endOfTrack;
            }
        }
    }

    public void Start(TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, TimeSpan.Zero);
        CancellationTokenSource? fade;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            fade = ResetNoLock();
            var generation = _generation;
            _endsAt = _time.GetUtcNow() + duration;
            _timer = _time.CreateTimer(_ => OnExpired(generation), null, duration, Timeout.InfiniteTimeSpan);
        }

        fade?.Cancel();
        _logger.LogInformation("Sleep timer set for {Duration}", duration);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void StopAtEndOfTrack()
    {
        CancellationTokenSource? fade;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            fade = ResetNoLock();
            _endOfTrack = true;
            _player.PauseAtEndOfTrack = true;
        }

        fade?.Cancel();
        _logger.LogInformation("Sleep timer set for the end of the track");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Cancel()
    {
        CancellationTokenSource? fade;
        bool wasActive;
        lock (_gate)
        {
            wasActive = _endsAt is not null || _endOfTrack;
            fade = ResetNoLock();
        }

        fade?.Cancel();
        if (wasActive)
        {
            _logger.LogInformation("Sleep timer cancelled");
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? fade;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            fade = ResetNoLock();
        }

        _player.TrackCompleted -= OnTrackCompleted;
        fade?.Cancel();
    }

    /// <summary>Stops whatever is running (timer, fade, end-of-track flag). The caller cancels the returned fade outside the lock.</summary>
    private CancellationTokenSource? ResetNoLock()
    {
        _generation++;
        _timer?.Dispose();
        _timer = null;
        if (_endOfTrack)
        {
            _endOfTrack = false;
            _player.PauseAtEndOfTrack = false;
        }

        _endsAt = null;
        var fade = _fade;
        _fade = null;
        return fade;
    }

    private void OnExpired(long generation)
    {
        CancellationTokenSource fade;
        lock (_gate)
        {
            if (generation != _generation || _endsAt is null || _fade is not null)
            {
                return;
            }

            _timer?.Dispose();
            _timer = null;
            fade = _fade = new CancellationTokenSource();
        }

        _logger.LogInformation("Sleep timer fired: fading out over {Duration}", FadeDuration);
        _ = FadeAsync(generation, fade.Token);
    }

    private async Task FadeAsync(long generation, CancellationToken cancellationToken)
    {
        try
        {
            await _player.FadeOutAndPauseAsync(FadeDuration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // Cancel() or a new timer took over and already reported it
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sleep timer fade failed; pausing directly");
            try
            {
                _player.Pause();
            }
            catch (Exception pauseEx)
            {
                _logger.LogWarning(pauseEx, "Sleep timer could not pause playback");
            }
        }

        lock (_gate)
        {
            if (generation != _generation)
            {
                return;
            }

            ResetNoLock();
        }

        _logger.LogInformation("Sleep timer paused playback");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnTrackCompleted(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            // Still set means the player did not apply it to this end (it was armed just after the track ended).
            if (!_endOfTrack || _player.PauseAtEndOfTrack)
            {
                return;
            }

            _endOfTrack = false;
            ResetNoLock();
        }

        _logger.LogInformation("Sleep timer paused playback at the end of {VideoId}", e.Track?.VideoId);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
