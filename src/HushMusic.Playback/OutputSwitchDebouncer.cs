namespace HushMusic.Playback;

/// <summary>What to do about a change of the audio outputs (<see cref="OutputSwitchDebouncer"/>).</summary>
internal enum OutputSwitchStep
{
    /// <summary>The players are already where they should be (a pending move back was dropped).</summary>
    None,

    /// <summary>Move now: the output in use went away, so the players have nothing to play on.</summary>
    Now,

    /// <summary>The chosen output is back: move once it has stayed for <see cref="OutputSwitchDebouncer.SettleTime"/>.</summary>
    Later,
}

/// <summary>
/// Decides when the players follow the chosen audio output as it comes and goes. Losing it moves them to the system
/// default at once; its return is held back until it has stayed for <see cref="SettleTime"/>. A Bluetooth speaker
/// that flaps then moves playback away once and back once, after it has made up its mind, instead of on every change
/// (each move reconnects a live station), and a speaker that has only just reconnected gets a moment before audio is
/// sent to it. Thread-safe.
/// </summary>
/// <remarks>The first choice of output, and a change of the setting itself, don't go through this.</remarks>
internal sealed class OutputSwitchDebouncer : IDisposable
{
    public static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Action _switch;
    private DateTimeOffset? _dueAt; // a move back is pending
    private bool _disposed;

    /// <param name="time">The clock.</param>
    /// <param name="switch">Moves the players to the output that is wanted by then; runs on a thread-pool thread.</param>
    public OutputSwitchDebouncer(TimeProvider time, Action @switch)
    {
        _time = time;
        _switch = @switch;
        _timer = time.CreateTimer(_ => Fire(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>A move back to the chosen output is waiting for it to settle.</summary>
    public bool IsPending
    {
        get
        {
            lock (_gate)
            {
                return _dueAt is not null;
            }
        }
    }

    /// <summary>
    /// The outputs changed: <paramref name="wanted"/> is where the players should play now, <paramref name="inUse"/>
    /// where they play (null = the system default). With <see cref="OutputSwitchStep.Now"/> the caller moves the
    /// players itself; with <see cref="OutputSwitchStep.Later"/> the switch callback does, unless the outputs change
    /// back first. Each change restarts the wait.
    /// </summary>
    public OutputSwitchStep OnOutputsChanged(string? wanted, string? inUse)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return OutputSwitchStep.None;
            }

            if (AudioOutputChoice.SameDevice(wanted, inUse) || wanted is null)
            {
                _dueAt = null;
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                return wanted is null && inUse is not null ? OutputSwitchStep.Now : OutputSwitchStep.None;
            }

            _dueAt = _time.GetUtcNow() + SettleTime;
            _timer.Change(SettleTime, Timeout.InfiniteTimeSpan);
            return OutputSwitchStep.Later;
        }
    }

    /// <summary>Drops a pending move (the output was decided another way).</summary>
    public void Cancel()
    {
        lock (_gate)
        {
            _dueAt = null;
            if (!_disposed)
            {
                _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _dueAt = null;
        }

        _timer.Dispose();
    }

    private void Fire()
    {
        lock (_gate)
        {
            if (_dueAt is not { } due || _disposed)
            {
                return;
            }

            // A change raced with this callback and restarted the wait.
            var left = due - _time.GetUtcNow();
            if (left > TimeSpan.Zero)
            {
                _timer.Change(left, Timeout.InfiniteTimeSpan);
                return;
            }

            _dueAt = null;
        }

        _switch();
    }
}
