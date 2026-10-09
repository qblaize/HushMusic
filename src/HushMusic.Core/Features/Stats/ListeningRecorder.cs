using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.Stats;

/// <summary>
/// Writes every play to the local <see cref="IPlayLog"/> for "Your stats". Listening time is measured from the player's
/// state (time spent in <see cref="PlaybackStatus.Playing"/>), never from track ends alone, so skips, pauses, seeks and
/// crossfades are counted for what was actually heard. A play is logged once it counts (<see cref="PlayRules"/>), every
/// few minutes while it goes on, and when it ends; a crash loses at most the last few minutes.
/// </summary>
public sealed partial class ListeningRecorder : IHostedService, IDisposable
{
    /// <summary>How often the time of a long play (a radio station, a mix) is written while it goes on.</summary>
    internal static readonly TimeSpan CheckpointInterval = TimeSpan.FromMinutes(5);

    /// <summary>Listening is added up on this beat while playing (it stops otherwise), and on every player state change.</summary>
    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The most one beat can add. A longer gap means the clock jumped without anyone listening (the PC slept while the
    /// player still said Playing).
    /// </summary>
    internal static readonly TimeSpan MaxTick = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IPlayer _player;
    private readonly IPlayLog _log;
    private readonly TimeProvider _time;
    private readonly ILogger<ListeningRecorder> _logger;
    private readonly Lock _gate = new();
    private Session? _session;
    private bool _isPlaying;
    private ITimer? _timer;
    private Task _writes = Task.CompletedTask;

    public ListeningRecorder(IPlayer player, IPlayLog log, TimeProvider time, ILogger<ListeningRecorder> logger)
    {
        _player = player;
        _log = log;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _player.TrackChanged += OnTrackChanged;
        _player.TrackStarted += OnTrackStarted;
        _player.TrackCompleted += OnTrackCompleted;
        _player.StatusChanged += OnStatusChanged;
        _player.PlaybackFailed += OnPlaybackFailed;
        lock (_gate)
        {
            _isPlaying = _player.Status == PlaybackStatus.Playing;
            _timer = _time.CreateTimer(_ => OnTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            UpdateBeatNoLock();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _player.TrackChanged -= OnTrackChanged;
        _player.TrackStarted -= OnTrackStarted;
        _player.TrackCompleted -= OnTrackCompleted;
        _player.StatusChanged -= OnStatusChanged;
        _player.PlaybackFailed -= OnPlaybackFailed;

        Task writes;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            CloseNoLock();
            writes = _writes;
        }

        // The app is closing: give the last play a moment to reach the disk.
        await Task.WhenAny(writes, Task.Delay(StopTimeout, _time, cancellationToken)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Completes when every line queued so far is written (tests).</summary>
    internal Task WhenWritten()
    {
        lock (_gate)
        {
            return _writes;
        }
    }

    // Player events arrive in order on the player's event thread; the beat comes from a timer thread.

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            // A new play, also when repeat-one replays the same item.
            CloseNoLock();
            if (e.Track is { } track)
            {
                _isPlaying = true;
                _session = new Session(track, e.Item?.Id, _time.GetLocalNow(), _time.GetTimestamp());
                UpdateBeatNoLock();
            }
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_session is { } session && !session.IsFor(e.Item, e.Track))
            {
                CloseNoLock();
            }
        }
    }

    private void OnTrackCompleted(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            // With crossfade the next song may already have started: only end the play this event is about.
            if (_session is { } session && session.IsFor(e.Item, e.Track))
            {
                CloseNoLock();
            }
        }
    }

    private void OnPlaybackFailed(object? sender, PlaybackErrorEventArgs e)
    {
        lock (_gate)
        {
            if (_session is { } session && (e.Track is null || session.IsFor(null, e.Track)))
            {
                CloseNoLock();
            }
        }
    }

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            // Count up to now before the state flips, so the last stretch of playing time isn't lost.
            TickNoLock();
            _isPlaying = e.Status == PlaybackStatus.Playing;
            UpdateBeatNoLock();
            if (_session is { } session)
            {
                session.LastTick = _time.GetTimestamp();
            }
            else if (_isPlaying && _player.CurrentTrack is { } track)
            {
                // Playing again without a new start (e.g. the same song played again after the queue ended).
                _session = new Session(track, null, _time.GetLocalNow(), _time.GetTimestamp());
            }
        }
    }

    private void OnTick()
    {
        lock (_gate)
        {
            TickNoLock();
        }
    }

    // Paused or stopped there is nothing to add up, so the beat (a wakeup every second) only runs while playing.
    private void UpdateBeatNoLock()
    {
        var interval = _isPlaying ? TickInterval : Timeout.InfiniteTimeSpan;
        _timer?.Change(interval, interval);
    }

    private void TickNoLock()
    {
        if (_session is not { } session)
        {
            return;
        }

        var now = _time.GetTimestamp();
        if (_isPlaying)
        {
            var elapsed = _time.GetElapsedTime(session.LastTick, now);
            if (elapsed > TimeSpan.Zero)
            {
                session.Listened += elapsed < MaxTick ? elapsed : MaxTick;
            }
        }

        session.LastTick = now;
        if (_isPlaying && session.Duration is null && _player.Duration > TimeSpan.Zero && IsCurrent(session))
        {
            session.Duration = _player.Duration;
        }

        if (!session.Counted && PlayRules.CountsAsPlay(session.Listened, session.Duration))
        {
            session.Counted = true;
            WriteNoLock(session);
        }
        else if (session.Counted && session.Listened - session.Written >= CheckpointInterval)
        {
            WriteNoLock(session);
        }
    }

    private bool IsCurrent(Session session) => _player.CurrentTrack is { } current && current.VideoId == session.Track.VideoId;

    private void CloseNoLock()
    {
        if (_session is not { } session)
        {
            return;
        }

        TickNoLock();
        _session = null;
        if (session.Listened >= PlayRules.MinimumLogged || session.Counted)
        {
            if (session.Listened - session.Written >= TimeSpan.FromSeconds(1) || session.Written == TimeSpan.Zero)
            {
                WriteNoLock(session);
            }
        }
    }

    private void WriteNoLock(Session session)
    {
        session.Written = session.Listened;
        var record = PlayRecord.For(session.Id, session.Track, session.StartedAt, session.Listened, session.Duration);
        _writes = AppendAfterAsync(_writes, record);
    }

    // Lines are written one after another, in the order they were made.
    private async Task AppendAfterAsync(Task previous, PlayRecord record)
    {
        await previous.ConfigureAwait(false);
        try
        {
            await _log.AppendAsync(record).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogWriteFailed(_logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write to the play log")]
    private static partial void LogWriteFailed(ILogger logger, Exception exception);

    private sealed class Session(Track track, Guid? itemId, DateTimeOffset startedAt, long timestamp)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public Track Track { get; } = track;

        public Guid? ItemId { get; } = itemId;

        public DateTimeOffset StartedAt { get; } = new(startedAt.Ticks - (startedAt.Ticks % TimeSpan.TicksPerSecond), startedAt.Offset);

        public TimeSpan? Duration { get; set; } = track.Duration is { } length && length > TimeSpan.Zero ? length : null;

        public TimeSpan Listened { get; set; }

        public TimeSpan Written { get; set; }

        public bool Counted { get; set; }

        public long LastTick { get; set; } = timestamp;

        // Same queue item when both sides know it; otherwise the same song or station.
        public bool IsFor(QueueItem? item, Track? track) =>
            item is not null && ItemId is { } id ? item.Id == id : track is not null && track.VideoId == Track.VideoId;
    }
}
