using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

public enum PlaybackStatus
{
    Idle,
    Loading,
    Playing,
    Paused,
    Buffering,
    Ended,
    Failed,
}

public enum RepeatMode
{
    Off,
    All,
    One,
}

/// <summary>A playable audio URL for one track. URLs expire; check <see cref="ExpiresAt"/>.</summary>
public sealed record ResolvedStream
{
    public required string VideoId { get; init; }

    public required Uri Url { get; init; }

    public string? Container { get; init; }

    public string? Codec { get; init; }

    public int? BitrateKbps { get; init; }

    public TimeSpan? Duration { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>HTTP headers the stream host expects (user-agent etc.).</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>A live radio stream: endless, no duration, never expires.</summary>
    public bool IsLive { get; init; }
}

/// <summary>All stream resolution goes through this one interface. Implemented by HushMusic.Playback (yt-dlp).</summary>
public interface IStreamResolver
{
    /// <summary>Returns a cached stream if it is still valid, otherwise resolves a new one.</summary>
    Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Resolves the stream of a queue item. Live radio stations (<see cref="Track.Station"/>) carry their own URL; the
    /// player always calls this overload. (The default implementation, for test fakes, resolves by video id.)
    /// </summary>
    Task<ResolvedStream> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        return ResolveAsync(track.VideoId, cancellationToken);
    }

    /// <summary>Drops the cached stream (e.g. after an HTTP 403) so the next resolve is fresh.</summary>
    void Invalidate(string videoId);

    /// <summary>Resolves in the background so the next track starts instantly. Never throws.</summary>
    void Prefetch(string videoId);

    /// <summary>Version string of the resolver backend (yt-dlp), or null if it is missing.</summary>
    Task<string?> GetBackendVersionAsync(CancellationToken cancellationToken = default);

    /// <summary>Updates the backend. Returns true if a newer version was installed.</summary>
    Task<bool> UpdateBackendAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Audio output. Plays the current item of <see cref="IQueueService"/> and moves the queue
/// cursor on next/previous/track end. Implemented by HushMusic.Playback (MediaPlayer + SMTC).
/// Events are raised on a background thread.
/// </summary>
/// <remarks>
/// With crossfade on (<see cref="AppSettings.CrossfadeSeconds"/>) the next track starts that many seconds before the
/// current one ends, and becomes current right then: the queue advances, <see cref="TrackCompleted"/> is raised for the
/// outgoing track, then <see cref="TrackChanged"/> and (once its audio runs) <see cref="TrackStarted"/> for the incoming
/// one. From that moment <see cref="CurrentTrack"/>, <see cref="Position"/>, <see cref="Duration"/>,
/// <see cref="PositionChanged"/> and <see cref="Status"/> describe the incoming track; the outgoing one is only heard
/// under it until the fade ends. With crossfade off, the next track is opened ahead of time and starts as soon as the
/// current one ends (the events are the same as without preloading).
/// </remarks>
public interface IPlayer
{
    PlaybackStatus Status { get; }

    Track? CurrentTrack { get; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    /// <summary>
    /// True while the current item is a live radio stream: <see cref="Duration"/> is zero, <see cref="Seek"/> does
    /// nothing and pausing drops the connection (Play reconnects to the live edge).
    /// </summary>
    bool IsLive => CurrentTrack?.IsLiveRadio == true;

    /// <summary>The user's volume, 0.0 – 1.0. Volume normalization, crossfades and the sleep-timer fade are applied on top of it.</summary>
    double Volume { get; set; }

    bool IsMuted { get; set; }

    /// <summary>Shortcut to <see cref="IQueueService.IsShuffled"/> / <see cref="IQueueService.SetShuffle"/>.</summary>
    bool IsShuffleEnabled { get; set; }

    /// <summary>Shortcut to <see cref="IQueueService.RepeatMode"/>.</summary>
    RepeatMode RepeatMode { get; set; }

    /// <summary>
    /// When true, the next time a track plays to its end playback stops instead of continuing: the next queue item
    /// becomes current (shown at 0:00, not started) and Play continues with it. Skips don't count. The player resets
    /// the flag to false when it applies it, before raising <see cref="TrackCompleted"/>. Used by the sleep timer.
    /// (The default implementation, for test fakes, ignores it.)
    /// </summary>
    bool PauseAtEndOfTrack
    {
        get => false;
        set { }
    }

    event EventHandler<PlaybackStatusChangedEventArgs>? StatusChanged;

    /// <summary>A new track became current (it may still be loading). With crossfade on, when the fade into it starts.</summary>
    event EventHandler<TrackChangedEventArgs>? TrackChanged;

    /// <summary>Audio actually started for the current track (first time only).</summary>
    event EventHandler<TrackChangedEventArgs>? TrackStarted;

    /// <summary>
    /// The current track played to its end, or, with crossfade on, reached the start of the fade into the next track
    /// (its last seconds are then heard under the next one, which is already current). Never raised for a skip.
    /// </summary>
    event EventHandler<TrackChangedEventArgs>? TrackCompleted;

    /// <summary>Raised about 4 times per second while playing.</summary>
    event EventHandler<PositionChangedEventArgs>? PositionChanged;

    event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed;

    /// <summary>Resumes, or starts the queue's current item.</summary>
    Task PlayAsync(CancellationToken cancellationToken = default);

    /// <summary>Makes queue item <paramref name="index"/> current and plays it.</summary>
    Task PlayQueueIndexAsync(int index, CancellationToken cancellationToken = default);

    void Pause();

    Task TogglePlayPauseAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the playback position. When the current track is not loaded yet (e.g. a restored session), this sets
    /// where <see cref="PlayAsync"/> will start it; <see cref="Position"/> reports it and <see cref="PositionChanged"/> is raised.
    /// </summary>
    void Seek(TimeSpan position);

    /// <summary>
    /// Fades the audio out over <paramref name="duration"/>, pauses, then brings the volume back for the next play.
    /// Uses an internal gain: <see cref="Volume"/> (the user's volume) never changes. Pauses at once when nothing is playing.
    /// Cancelling restores the volume (short ramp), keeps playing and throws <see cref="OperationCanceledException"/>.
    /// (The default implementation, for test fakes, just pauses.)
    /// </summary>
    Task FadeOutAndPauseAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        Pause();
        return Task.CompletedTask;
    }

    Task NextAsync(CancellationToken cancellationToken = default);

    /// <summary>Restarts the track if more than ~3 s in, otherwise goes to the previous item.</summary>
    Task PreviousAsync(CancellationToken cancellationToken = default);

    void Stop();
}

/// <summary>High-level "play this" entry points used by pages and features. Implemented in Core.</summary>
public interface IPlaybackActions
{
    /// <summary>Replaces the queue with <paramref name="tracks"/> and plays from <paramref name="startIndex"/>.</summary>
    Task PlayTracksAsync(IReadOnlyList<Track> tracks, int startIndex = 0, QueueSource? source = null, CancellationToken cancellationToken = default);

    /// <summary>YouTube Music behaviour for a single song: play it and fill "up next" from the watch endpoint.</summary>
    Task PlayTrackWithUpNextAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>Starts an endless radio seeded by the track.</summary>
    Task StartRadioAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>Plays a playlist (also album audio playlists and radio/mix ids) through the watch endpoint.</summary>
    Task PlayPlaylistAsync(string playlistId, bool shuffle = false, CancellationToken cancellationToken = default);

    Task PlayAlbumAsync(Album album, bool shuffle = false, CancellationToken cancellationToken = default);

    void AddToQueue(IReadOnlyList<Track> tracks);

    void PlayNext(IReadOnlyList<Track> tracks);
}
