using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Tests;

internal static class TestData
{
    public static Track Track(string videoId, bool available = true) => new()
    {
        Title = "Song " + videoId,
        VideoId = videoId,
        IsAvailable = available,
    };

    public static Track[] Tracks(params string[] videoIds) => [.. videoIds.Select(id => Track(id))];

    public static string[] Ids(IEnumerable<QueueItem> items) => [.. items.Select(i => i.Track.VideoId)];
}

internal sealed record FadeCall(TimeSpan Duration, CancellationToken Token, TaskCompletionSource Completion);

internal sealed class FakePlayer : IPlayer
{
    public List<int> PlayedIndices { get; } = [];

    public List<TimeSpan> Seeks { get; } = [];

    public List<FadeCall> Fades { get; } = [];

    public int PauseCount { get; private set; }

    /// <summary>Lets a test hold PlayQueueIndexAsync open.</summary>
    public Func<int, CancellationToken, Task> OnPlayQueueIndex { get; set; } = (_, _) => Task.CompletedTask;

    public PlaybackStatus Status { get; set; } = PlaybackStatus.Idle;

    public Track? CurrentTrack { get; set; }

    public TimeSpan Position { get; set; }

    public TimeSpan Duration { get; set; }

    public double Volume { get; set; }

    public bool IsMuted { get; set; }

    public bool IsShuffleEnabled { get; set; }

    public RepeatMode RepeatMode { get; set; }

    public bool PauseAtEndOfTrack { get; set; }

    public event EventHandler<PlaybackStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;

    public event EventHandler<TrackChangedEventArgs>? TrackStarted;

    public event EventHandler<TrackChangedEventArgs>? TrackCompleted;

    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    public event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed;

    public void RaiseTrackStarted(Track track) => TrackStarted?.Invoke(this, new TrackChangedEventArgs(track, QueueItem.Create(track)));

    public void RaiseTrackChanged(Track? track) => TrackChanged?.Invoke(this, new TrackChangedEventArgs(track, track is null ? null : QueueItem.Create(track)));

    /// <summary>Like the real player: a pending <see cref="PauseAtEndOfTrack"/> is consumed before TrackCompleted is raised.</summary>
    public void RaiseTrackCompleted(Track track, bool consumePauseAtEnd = true)
    {
        if (consumePauseAtEnd)
        {
            PauseAtEndOfTrack = false;
        }

        TrackCompleted?.Invoke(this, new TrackChangedEventArgs(track, QueueItem.Create(track)));
    }

    public void RaiseStatusChanged(PlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, new PlaybackStatusChangedEventArgs(status));
    }

    public void RaisePositionChanged(TimeSpan position)
    {
        Position = position;
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(position, Duration));
    }

    public void RaisePlaybackFailed(Track track) => PlaybackFailed?.Invoke(this, new PlaybackErrorEventArgs(track, "failed", null));

    /// <summary>Records the call; the returned task completes when the test completes <see cref="FadeCall.Completion"/>, or is cancelled with the token.</summary>
    public Task FadeOutAndPauseAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        lock (Fades)
        {
            Fades.Add(new FadeCall(duration, cancellationToken, completion));
        }

        return FadeAsync();

        async Task FadeAsync()
        {
            await completion.Task;
            Pause();
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PlayQueueIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        lock (PlayedIndices)
        {
            PlayedIndices.Add(index);
        }

        return OnPlayQueueIndex(index, cancellationToken);
    }

    public void Pause()
    {
        PauseCount++;
        if (Status is PlaybackStatus.Playing or PlaybackStatus.Buffering)
        {
            Status = PlaybackStatus.Paused;
        }
    }

    public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Seek(TimeSpan position)
    {
        lock (Seeks)
        {
            Seeks.Add(position);
        }

        Position = position;
    }

    public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Stop()
    {
    }
}

internal sealed record WatchCall(string? VideoId, string? PlaylistId, bool Radio, bool Shuffle, CancellationToken Token);

internal sealed class FakeWatchApi : IWatchApi
{
    public List<WatchCall> Calls { get; } = [];

    public List<string> ContinuationCalls { get; } = [];

    public Func<WatchCall, Task<WatchPlaylist>> OnGet { get; set; } = _ => Task.FromResult(new WatchPlaylist());

    public Func<string, CancellationToken, Task<Paged<Track>>> OnContinuation { get; set; } = (_, _) => Task.FromResult(Paged<Track>.Empty);

    public List<string> LoudnessCalls { get; } = [];

    public Func<string, CancellationToken, Task<double?>> OnLoudness { get; set; } = (_, _) => Task.FromResult<double?>(null);

    public Task<WatchPlaylist> GetWatchPlaylistAsync(string? videoId, string? playlistId = null, bool radio = false, bool shuffle = false, CancellationToken cancellationToken = default)
    {
        var call = new WatchCall(videoId, playlistId, radio, shuffle, cancellationToken);
        lock (Calls)
        {
            Calls.Add(call);
        }

        return OnGet(call);
    }

    public Task<Paged<Track>> GetWatchPlaylistContinuationAsync(string continuation, CancellationToken cancellationToken = default)
    {
        lock (ContinuationCalls)
        {
            ContinuationCalls.Add(continuation);
        }

        return OnContinuation(continuation, cancellationToken);
    }

    public Task<Lyrics?> GetLyricsAsync(string lyricsBrowseId, bool timestamps = false, CancellationToken cancellationToken = default) => Task.FromResult<Lyrics?>(null);

    public Task<IReadOnlyList<Shelf>> GetRelatedAsync(string relatedBrowseId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Shelf>>([]);

    public Task<double?> GetLoudnessDbAsync(string videoId, CancellationToken cancellationToken = default)
    {
        lock (LoudnessCalls)
        {
            LoudnessCalls.Add(videoId);
        }

        return OnLoudness(videoId, cancellationToken);
    }
}

internal sealed class FakeBrowseApi : IBrowseApi
{
    public Dictionary<string, AlbumPage> Albums { get; } = [];

    public List<string> AlbumCalls { get; } = [];

    public Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken cancellationToken = default)
    {
        AlbumCalls.Add(browseId);
        return Task.FromResult(Albums[browseId]);
    }

    public Task<Paged<Shelf>> GetHomeAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<ArtistPage> GetArtistAsync(string channelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<Paged<Track>> GetPlaylistTracksAsync(string continuation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

internal sealed class FakeNotifications : INotificationService
{
    public List<AppNotification> Shown { get; } = [];

    public event EventHandler<AppNotification>? Raised { add { } remove { } }

    public void Show(AppNotification notification)
    {
        lock (Shown)
        {
            Shown.Add(notification);
        }
    }

    public void ShowError(string title, Exception exception) => Show(new AppNotification(NotificationSeverity.Error, title, exception.Message, exception));

    public void ShowInfo(string title, string message) => Show(new AppNotification(NotificationSeverity.Informational, title, message));
}

internal sealed class FakeSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed { add { } remove { } }

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        update(Current);
        return Task.CompletedTask;
    }
}

internal sealed class FakeAuth : IAuthService
{
    public AuthStatus Status { get; set; } = AuthStatus.SignedIn;

    public AuthMode Mode => AuthMode.Cookies;

    public event EventHandler<AuthStatusChangedEventArgs>? StatusChanged { add { } remove { } }

    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignInWithCookiesAsync(IReadOnlyList<BrowserCookie> cookies, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignInWithCookieHeaderAsync(string cookieHeader, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task SignOutAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

internal sealed class FakeAccountApi : IAccountApi
{
    public List<string> HistoryItems { get; } = [];

    public Func<string, Task> OnAddHistory { get; set; } = _ => Task.CompletedTask;

    public Task AddHistoryItemAsync(string videoId, CancellationToken cancellationToken = default)
    {
        lock (HistoryItems)
        {
            HistoryItems.Add(videoId);
        }

        return OnAddHistory(videoId);
    }

    public Task<AccountInfo?> GetAccountInfoAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RateSongAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task AddPlaylistItemsAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task RemovePlaylistItemsAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

    public Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}

/// <summary>A clock that only moves when the test calls <see cref="Advance"/>; due timers fire on the calling thread.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + by;
        }

        while (true)
        {
            ManualTimer? due;
            lock (_gate)
            {
                due = _timers.Where(t => t.DueAt is { } at && at <= target).MinBy(t => t.DueAt);
                if (due is null)
                {
                    _now = target;
                    return;
                }

                _now = due.DueAt!.Value;
                due.ScheduleNext();
            }

            due.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period;

        // Guarded by owner._gate.
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                _period = period;
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner._now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            }

            return true;
        }

        public void ScheduleNext() => DueAt = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : DueAt + _period;

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate)
            {
                DueAt = null;
                owner._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
