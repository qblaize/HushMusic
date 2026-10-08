using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>How plays and listening time are recorded from the player's events (ListeningRecorder).</summary>
public sealed class ListeningRecorderTests : IAsyncLifetime
{
    private readonly ScriptedPlayer _player = new();
    private readonly SteppedTime _time = new();
    private readonly MemoryPlayLog _log = new();
    private readonly ListeningRecorder _recorder;

    public ListeningRecorderTests()
    {
        _recorder = new ListeningRecorder(_player, _log, _time, NullLogger<ListeningRecorder>.Instance);
    }

    public async ValueTask InitializeAsync() => await _recorder.StartAsync(TestContext.Current.CancellationToken);

    public ValueTask DisposeAsync()
    {
        _recorder.Dispose();
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task A_song_is_logged_when_it_counts_and_again_when_it_ends()
    {
        var item = _player.Start(Song("a", seconds: 200));
        Listen(29);
        await _recorder.WhenWritten();
        Assert.Empty(_log.Lines);

        Listen(1);
        await _recorder.WhenWritten();
        var counted = Assert.Single(_log.Lines);
        Assert.Equal(30, counted.ListenedSeconds);
        Assert.Equal(PlayKind.Track, counted.Kind);
        Assert.True(counted.IsPlay);

        Listen(15);
        _player.Complete(item);
        await _recorder.WhenWritten();

        Assert.Equal(2, _log.Lines.Count);
        Assert.Equal(counted.Id, _log.Lines[1].Id);
        Assert.Equal(45, _log.Lines[1].ListenedSeconds);
        Assert.Equal(200, _log.Lines[1].DurationSeconds);
    }

    [Fact]
    public async Task Pauses_and_buffering_do_not_count()
    {
        var item = _player.Start(Song("a", seconds: 300));
        Listen(10);
        _player.SetStatus(PlaybackStatus.Paused);
        Wait(120);
        _player.SetStatus(PlaybackStatus.Playing);
        Listen(5);
        _player.SetStatus(PlaybackStatus.Buffering);
        Wait(30);
        _player.SetStatus(PlaybackStatus.Playing);
        Listen(10);
        _player.Complete(item);
        await _recorder.WhenWritten();

        var record = Assert.Single(_log.Lines);
        Assert.Equal(25, record.ListenedSeconds);
        Assert.False(record.IsPlay);
    }

    [Fact]
    public async Task Quick_skips_are_not_logged()
    {
        _player.Start(Song("a", seconds: 200));
        Listen(6);
        _player.Start(Song("b", seconds: 200));
        Listen(3);
        _player.Change(Song("c", seconds: 200));
        await _recorder.WhenWritten();

        Assert.Empty(_log.Lines);
    }

    [Fact]
    public async Task Listening_that_does_not_count_is_still_logged_for_the_time()
    {
        _player.Start(Song("a", seconds: 200));
        Listen(12);
        _player.Change(Song("b", seconds: 200));
        await _recorder.WhenWritten();

        var record = Assert.Single(_log.Lines);
        Assert.Equal(12, record.ListenedSeconds);
        Assert.False(record.IsPlay);
    }

    [Fact]
    public async Task A_short_song_counts_after_half_its_length()
    {
        _player.Start(Song("a", seconds: 40));
        Listen(20);
        await _recorder.WhenWritten();

        Assert.True(Assert.Single(_log.Lines).IsPlay);
    }

    [Fact]
    public async Task The_length_comes_from_the_player_when_the_song_has_none()
    {
        _player.Duration = TimeSpan.FromSeconds(30);
        _player.Start(Song("a", seconds: null));
        Listen(15);
        await _recorder.WhenWritten();

        var record = Assert.Single(_log.Lines);
        Assert.Equal(30, record.DurationSeconds);
        Assert.True(record.IsPlay);
    }

    [Fact]
    public async Task A_sleeping_PC_adds_no_listening_time()
    {
        var item = _player.Start(Song("a", seconds: 600));
        Listen(40);
        Wait(TimeSpan.FromHours(2), tick: true);
        Listen(10);
        _player.Complete(item);
        await _recorder.WhenWritten();

        Assert.InRange(_log.Lines[^1].ListenedSeconds, 50, 50 + (int)ListeningRecorder.MaxTick.TotalSeconds);
    }

    [Fact]
    public async Task Radio_is_logged_as_station_time_with_checkpoints()
    {
        var station = new RadioStation { Id = "st-1", Name = "Deep FM", StreamUrl = "http://example.invalid/stream", LogoUrl = "http://example.invalid/logo.png" };
        _player.Start(station.ToTrack());
        Listen(12 * 60);
        _player.Change(Song("a", seconds: 200));
        await _recorder.WhenWritten();

        Assert.All(_log.Lines, r => Assert.Equal(PlayKind.Radio, r.Kind));
        Assert.All(_log.Lines, r => Assert.Equal("st-1", r.ItemId));
        Assert.Equal([30, 330, 630, 720], _log.Lines.Select(r => r.ListenedSeconds));
        Assert.Equal("Deep FM", _log.Lines[^1].Title);
        Assert.Equal("http://example.invalid/logo.png", _log.Lines[^1].ArtUrl);
        Assert.Empty(_log.Lines[^1].Artists);
        Assert.Single(_log.Lines.Select(r => r.Id).Distinct());
    }

    [Fact]
    public async Task With_crossfade_the_old_song_ends_when_the_next_starts()
    {
        var first = _player.Start(Song("a", seconds: 200));
        Listen(60);
        var second = _player.Start(Song("b", seconds: 200));
        Listen(3);

        // The fade-out of the first song finishes after the second one started: it must not end the second play.
        _player.Complete(first);
        Listen(40);
        _player.Complete(second);
        await _recorder.WhenWritten();

        var plays = _log.Lines.GroupBy(r => r.Id).Select(g => g.MaxBy(r => r.ListenedSeconds)!).ToList();
        Assert.Equal(["a", "b"], plays.Select(p => p.ItemId));
        Assert.Equal([60, 43], plays.Select(p => p.ListenedSeconds));
    }

    [Fact]
    public async Task Replaying_the_same_item_is_a_new_play()
    {
        var item = _player.Start(Song("a", seconds: 60));
        Listen(60);
        _player.Restart(item);
        Listen(40);
        _player.Complete(item);
        await _recorder.WhenWritten();

        Assert.Equal(2, _log.Lines.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Playing_again_without_a_start_event_is_counted_too()
    {
        var item = _player.Start(Song("a", seconds: 300));
        Listen(40);
        _player.Complete(item);
        _player.SetStatus(PlaybackStatus.Ended);
        Wait(10);
        _player.SetStatus(PlaybackStatus.Playing);
        Listen(35);
        await _recorder.WhenWritten();

        Assert.Equal(2, _log.Lines.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public async Task Stopping_the_app_writes_the_current_play()
    {
        _player.Start(Song("a", seconds: 300));
        Listen(95);

        await _recorder.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(95, _log.Lines[^1].ListenedSeconds);
    }

    [Fact]
    public async Task Track_metadata_is_kept()
    {
        var song = Song("a", seconds: 200) with
        {
            Title = "Song A",
            Artists = [new ArtistRef("Artist 1", "UC1"), new ArtistRef("Artist 2", null)],
            Album = new AlbumRef("Album X", "MPREb_x"),
            Thumbnails = [new Thumbnail("https://img.invalid/60", 60, 60), new Thumbnail("https://img.invalid/226", 226, 226)],
        };
        _player.Start(song);
        Listen(31);
        await _recorder.WhenWritten();

        var record = Assert.Single(_log.Lines);
        Assert.Equal("Song A", record.Title);
        Assert.Equal([new PlayArtist("Artist 1", "UC1"), new PlayArtist("Artist 2", null)], record.Artists);
        Assert.Equal("Album X", record.Album);
        Assert.Equal("MPREb_x", record.AlbumId);
        Assert.Equal("https://img.invalid/226", record.ArtUrl);
        Assert.Equal(_time.StartedAt, record.Start);
    }

    private static Track Song(string videoId, int? seconds) =>
        TestData.Track(videoId) with { Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null };

    // One-second steps with the recorder's beat in between, as while playing.
    private void Listen(int seconds)
    {
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            _time.Tick();
        }
    }

    private void Wait(int seconds) => Listen(seconds);

    private void Wait(TimeSpan duration, bool tick)
    {
        _time.Advance(duration);
        if (tick)
        {
            _time.Tick();
        }
    }

    /// <summary>A clock that only moves when told; timers fire only on <see cref="Tick"/>.</summary>
    private sealed class SteppedTime : TimeProvider
    {
        private readonly List<SteppedTimer> _timers = [];
        private DateTimeOffset _now = new(2026, 10, 8, 18, 30, 0, TimeSpan.Zero);

        public DateTimeOffset StartedAt { get; } = new(2026, 10, 8, 18, 30, 0, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow() => _now;

        public override long GetTimestamp() => _now.UtcTicks;

        public void Advance(TimeSpan by) => _now += by;

        public void Tick()
        {
            foreach (var timer in _timers.ToList())
            {
                timer.Fire();
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new SteppedTimer(this, callback, state);
            _timers.Add(timer);
            return timer;
        }

        private sealed class SteppedTimer(SteppedTime owner, TimerCallback callback, object? state) : ITimer
        {
            public void Fire() => callback(state);

            public bool Change(TimeSpan dueTime, TimeSpan period) => true;

            public void Dispose() => owner._timers.Remove(this);

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }

    /// <summary>Raises player events with the same queue item from start to end, like the real player.</summary>
    private sealed class ScriptedPlayer : IPlayer
    {
        public PlaybackStatus Status { get; private set; }

        public Track? CurrentTrack { get; private set; }

        public TimeSpan Position => TimeSpan.Zero;

        public TimeSpan Duration { get; set; }

        public double Volume { get; set; }

        public bool IsMuted { get; set; }

        public bool IsShuffleEnabled { get; set; }

        public RepeatMode RepeatMode { get; set; }

        public event EventHandler<PlaybackStatusChangedEventArgs>? StatusChanged;

        public event EventHandler<TrackChangedEventArgs>? TrackChanged;

        public event EventHandler<TrackChangedEventArgs>? TrackStarted;

        public event EventHandler<TrackChangedEventArgs>? TrackCompleted;

        public event EventHandler<PositionChangedEventArgs>? PositionChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed
        {
            add { }
            remove { }
        }

        /// <summary>The track becomes current, loads and starts playing.</summary>
        public QueueItem Start(Track track)
        {
            var item = QueueItem.Create(track);
            Change(track, item);
            SetStatus(PlaybackStatus.Loading);
            SetStatus(PlaybackStatus.Playing);
            TrackStarted?.Invoke(this, new TrackChangedEventArgs(track, item));
            return item;
        }

        public void Restart(QueueItem item)
        {
            SetStatus(PlaybackStatus.Playing);
            TrackStarted?.Invoke(this, new TrackChangedEventArgs(item.Track, item));
        }

        public void Change(Track track, QueueItem? item = null)
        {
            CurrentTrack = track;
            TrackChanged?.Invoke(this, new TrackChangedEventArgs(track, item ?? QueueItem.Create(track)));
        }

        public void Complete(QueueItem item) => TrackCompleted?.Invoke(this, new TrackChangedEventArgs(item.Track, item));

        public void SetStatus(PlaybackStatus status)
        {
            Status = status;
            StatusChanged?.Invoke(this, new PlaybackStatusChangedEventArgs(status));
        }

        public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PlayQueueIndexAsync(int index, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Pause()
        {
        }

        public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Seek(TimeSpan position)
        {
        }

        public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public void Stop()
        {
        }
    }

    private sealed class MemoryPlayLog : IPlayLog
    {
        public List<PlayRecord> Lines { get; } = [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task AppendAsync(PlayRecord record, CancellationToken cancellationToken = default)
        {
            lock (Lines)
            {
                Lines.Add(record);
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<PlayRecord>> ReadAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlayRecord>>([.. Lines]);

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            Lines.Clear();
            return Task.CompletedTask;
        }
    }
}
