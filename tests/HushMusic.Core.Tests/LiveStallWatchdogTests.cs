using HushMusic.Core.Radio;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LiveStallWatchdogTests
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(250);
    private readonly ManualTimeProvider _time = new();
    private readonly LiveStallWatchdog _watchdog;

    public LiveStallWatchdogTests() => _watchdog = new LiveStallWatchdog(_time);

    [Fact]
    public void Watches_nothing_until_told()
    {
        _time.Advance(TimeSpan.FromMinutes(1));

        Assert.False(_watchdog.IsWatching);
        Assert.Null(_watchdog.Check());
        Assert.Null(_watchdog.DueIn());
    }

    [Fact]
    public void A_connection_that_never_plays_stalls_after_the_start_timeout()
    {
        _watchdog.Wait(restart: true);
        Assert.Equal(LiveStallWatchdog.StartTimeout, _watchdog.DueIn());

        _time.Advance(LiveStallWatchdog.StartTimeout - Tick);
        Assert.Null(_watchdog.Check());
        Assert.Equal(Tick, _watchdog.DueIn());

        _time.Advance(Tick);
        Assert.Equal(LiveStall.NoAudio, _watchdog.Check());
        Assert.Equal(TimeSpan.Zero, _watchdog.DueIn());
    }

    [Fact]
    public void Going_from_opening_to_buffering_does_not_buy_more_time()
    {
        _watchdog.Wait(restart: true); // connecting
        _time.Advance(TimeSpan.FromSeconds(8));
        _watchdog.Wait(); // opened, now buffering

        _time.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(LiveStall.NoAudio, _watchdog.Check());
    }

    [Fact]
    public void A_new_connection_gets_the_full_start_timeout()
    {
        _watchdog.Wait(restart: true);
        _time.Advance(TimeSpan.FromSeconds(10));

        // E.g. the audio output changed and the station connects again.
        _watchdog.Wait(restart: true);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Null(_watchdog.Check());
    }

    [Fact]
    public void Steady_playback_never_stalls()
    {
        _watchdog.Wait(restart: true);
        _time.Advance(TimeSpan.FromSeconds(1.3));
        _watchdog.Playing(TimeSpan.Zero);

        var position = TimeSpan.Zero;
        for (var i = 0; i < 4 * 60 * 10; i++) // ten minutes of position ticks
        {
            _time.Advance(Tick);
            position += Tick;
            Assert.True(_watchdog.Sample(position));
            Assert.Null(_watchdog.Check());
        }

        Assert.Equal(LiveStallWatchdog.FrozenTimeout, _watchdog.DueIn());
    }

    [Fact]
    public void Playing_with_a_frozen_position_stalls_after_the_frozen_timeout()
    {
        _watchdog.Playing(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromSeconds(30));
        _watchdog.Sample(TimeSpan.FromSeconds(30));

        // The server stops sending: Media Foundation keeps saying "playing" while the position stands still.
        for (var i = 0; i < 35; i++)
        {
            _time.Advance(Tick);
            Assert.False(_watchdog.Sample(TimeSpan.FromSeconds(30)));
            Assert.Null(_watchdog.Check());
        }

        _time.Advance(LiveStallWatchdog.FrozenTimeout - (35 * Tick));
        Assert.Equal(LiveStall.Frozen, _watchdog.Check());
    }

    [Fact]
    public void Playing_that_never_moves_from_the_start_stalls_too()
    {
        // Reported as playing right after opening, before any audio arrived, and then nothing came.
        _watchdog.Wait(restart: true);
        _time.Advance(TimeSpan.FromSeconds(1));
        _watchdog.Playing(TimeSpan.Zero);

        _time.Advance(LiveStallWatchdog.FrozenTimeout);

        Assert.Equal(LiveStall.Frozen, _watchdog.Check());
    }

    [Fact]
    public void A_position_that_starts_over_counts_as_progress()
    {
        _watchdog.Playing(TimeSpan.FromSeconds(100));
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.True(_watchdog.Sample(TimeSpan.FromSeconds(0.5)));
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(_watchdog.Check());
    }

    [Fact]
    public void Buffering_in_the_middle_gets_the_start_timeout_from_when_it_began()
    {
        _watchdog.Playing(TimeSpan.Zero);
        _time.Advance(TimeSpan.FromMinutes(2));
        _watchdog.Sample(TimeSpan.FromMinutes(2));

        _watchdog.Wait(); // rebuffering
        _time.Advance(LiveStallWatchdog.StartTimeout - Tick);
        Assert.Null(_watchdog.Check());

        _watchdog.Playing(TimeSpan.FromMinutes(2)); // back
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Null(_watchdog.Check());
    }

    [Fact]
    public void Stopping_forgets_the_connection()
    {
        _watchdog.Wait(restart: true);
        _time.Advance(TimeSpan.FromSeconds(5));
        _watchdog.Stop(); // paused

        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(_watchdog.IsWatching);
        Assert.Null(_watchdog.Check());
        Assert.Null(_watchdog.DueIn());
        Assert.False(_watchdog.Sample(TimeSpan.FromSeconds(1)));
    }
}
