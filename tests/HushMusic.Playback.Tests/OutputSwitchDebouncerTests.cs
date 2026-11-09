using Xunit;

namespace HushMusic.Playback.Tests;

/// <summary>
/// The chosen output is a Bluetooth speaker that comes and goes. The test plays the player's part: it reports each
/// change the device watcher would see and moves "the players" when told to. Every move reconnects a live station, so
/// a flapping speaker must cause as few as possible.
/// </summary>
public sealed class OutputSwitchDebouncerTests : IDisposable
{
    private const string Speaker = @"\\?\SWD#MMDEVAPI#{0.0.0.00000000}.{speaker}";
    private static readonly TimeSpan Settle = OutputSwitchDebouncer.SettleTime;
    private readonly ManualClock _clock = new();
    private readonly OutputSwitchDebouncer _debouncer;
    private readonly List<string> _moves = [];
    private string? _connected = Speaker; // what the watcher lists: the speaker, or nothing (null)
    private string? _inUse = Speaker;     // where the players play: the speaker, or the system default (null)

    public OutputSwitchDebouncerTests() => _debouncer = new OutputSwitchDebouncer(_clock, Move);

    public void Dispose() => _debouncer.Dispose();

    [Fact]
    public void Losing_the_speaker_moves_to_the_default_at_once()
    {
        Assert.Equal(OutputSwitchStep.Now, Report(null));
        Assert.Equal(["default"], _moves.ToArray());
    }

    [Fact]
    public void Its_return_moves_back_once_it_has_stayed_for_the_settle_time()
    {
        Report(null);
        _clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Equal(OutputSwitchStep.Later, Report(Speaker));
        _clock.Advance(Settle - TimeSpan.FromMilliseconds(1));
        Assert.Equal(["default"], _moves.ToArray());
        Assert.True(_debouncer.IsPending);

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(["default", "speaker"], _moves.ToArray());
        Assert.False(_debouncer.IsPending);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(2, _moves.Count);
    }

    [Fact]
    public void A_flapping_speaker_moves_playback_away_once_and_back_once()
    {
        // Drops and comes back every 0.6 s for 6 s, then stays.
        for (var i = 0; i < 10; i++)
        {
            Report(_connected is null ? Speaker : null);
            _clock.Advance(TimeSpan.FromSeconds(0.6));
        }

        Assert.Equal(Speaker, _connected);
        Assert.Equal(["default"], _moves.ToArray());

        _clock.Advance(Settle);
        Assert.Equal(["default", "speaker"], _moves.ToArray());
    }

    [Fact]
    public void A_speaker_that_returns_and_drops_again_moves_nothing_more()
    {
        Report(null);
        Assert.Equal(OutputSwitchStep.Later, Report(Speaker));
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(OutputSwitchStep.None, Report(null)); // gone again before it settled

        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(["default"], _moves.ToArray());
    }

    [Fact]
    public void Each_change_restarts_the_wait()
    {
        Report(null);
        Report(Speaker);
        _clock.Advance(TimeSpan.FromSeconds(1));
        Report(Speaker); // e.g. the speaker's name or state was updated
        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(_moves);

        _clock.Advance(Settle - TimeSpan.FromSeconds(1));
        Assert.Equal(2, _moves.Count);
    }

    [Fact]
    public void Changes_that_leave_the_players_where_they_are_do_nothing()
    {
        Assert.Equal(OutputSwitchStep.None, _debouncer.OnOutputsChanged(Speaker.ToUpperInvariant(), _inUse)); // ids compare without case
        Assert.Equal(OutputSwitchStep.None, _debouncer.OnOutputsChanged(null, null)); // already on the default
        _clock.Advance(TimeSpan.FromMinutes(1));

        Assert.Empty(_moves);
    }

    [Fact]
    public void Cancel_and_dispose_drop_a_pending_move()
    {
        Report(null);
        Report(Speaker);
        _debouncer.Cancel();
        _clock.Advance(Settle * 2);
        Assert.Single(_moves);

        Report(Speaker);
        _debouncer.Dispose();
        _clock.Advance(Settle * 2);
        Assert.Single(_moves);
        Assert.Equal(OutputSwitchStep.None, _debouncer.OnOutputsChanged(null, Speaker));
    }

    // The watcher's list changed: tell the debouncer, and move at once when it says so.
    private OutputSwitchStep Report(string? connected)
    {
        _connected = connected;
        var step = _debouncer.OnOutputsChanged(_connected, _inUse);
        if (step == OutputSwitchStep.Now)
        {
            Move();
        }

        return step;
    }

    private void Move()
    {
        _inUse = _connected;
        _moves.Add(_inUse is null ? "default" : "speaker");
    }

    /// <summary>A clock that only moves when told to; its timers fire during <see cref="Advance"/>.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 11, 9, 9, 40, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }

        public void Advance(TimeSpan by)
        {
            var target = _now + by;
            while (_timers.Where(t => t.DueAt <= target).MinBy(t => t.DueAt) is { } due)
            {
                _now = due.DueAt!.Value;
                due.DueAt = null; // one-shot timers only
                due.Fire();
            }

            _now = target;
        }

        private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
        {
            public DateTimeOffset? DueAt { get; set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock._now + dueTime;
                return true;
            }

            public void Fire() => callback(state);

            public void Dispose()
            {
                DueAt = null;
                clock._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
