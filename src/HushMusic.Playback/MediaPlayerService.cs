using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Radio;

namespace HushMusic.Playback;

/// <summary>
/// Plays the current item of <see cref="IQueueService"/> through <see cref="MediaPlayer"/> and mirrors it to the
/// system media controls. The queue is the source of truth: this class moves its cursor on next/previous/track end,
/// and follows it when anything else moves it (a feature, the queue panel, removing the current item).
/// </summary>
/// <remarks>
/// Concurrency model: every state change happens under <see cref="_gate"/>; the matching events are queued in
/// order while still under the lock and raised one by one on a background pump, never inside the lock.
/// Each load gets an id; anything that completes for an older id is ignored ("latest request wins").
/// Caller cancellation tokens only stop the caller from waiting: a page cancelling its work on navigation
/// must not stop the music. Loads are cancelled by newer loads, <see cref="Stop"/> and disposal.
/// <para>
/// Live radio items (<see cref="Track.IsLiveRadio"/>) differ: no duration and no seeking; pausing drops the connection
/// and Play reconnects at the live edge; a stream that drops or ends is reconnected with a backoff
/// (<see cref="LiveReconnectPolicy"/>) instead of moving on; the ICY title reader runs only while the station plays.
/// </para>
/// </remarks>
public sealed class MediaPlayerService : IPlayer, IDisposable
{
    private const string LoadIdKey = "HushMusic.LoadId";
    private const int MaxConsecutiveFailures = 3;
    private static readonly TimeSpan PositionInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan VolumeSaveDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RefreshBeforeExpiry = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DnsWarmUpTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan GainRampTime = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PauseSettleTime = TimeSpan.FromMilliseconds(300);

    // A live stream whose connection closes reports Paused, then MediaEnded; only a pause that outlasts this is real.
    private static readonly TimeSpan LivePauseConfirmTime = TimeSpan.FromSeconds(1.5);

    // Set while this class moves the queue cursor itself, so its own CurrentChanged is not treated as an external move.
    [ThreadStatic]
    private static bool t_movingQueue;

    private readonly object _gate = new();
    private readonly IQueueService _queue;
    private readonly IStreamResolver _resolver;
    private readonly ISettingsService _settings;
    private readonly VolumeNormalizer _normalizer;
    private readonly IRadioNowPlaying _radio;
    private readonly ILogger<MediaPlayerService> _logger;
    private readonly MediaPlayer _player;
    private readonly VolumeMixer _mixer;
    private readonly SmtcController _smtc;
    private readonly Timer _positionTimer;
    private readonly Timer _volumeSaveTimer;
    private readonly Channel<Action> _events = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });
    private readonly LiveReconnectPolicy _livePolicy = new(TimeProvider.System); // guarded by _gate

    // Guarded by _gate.
    private PlaybackStatus _status = PlaybackStatus.Idle;
    private QueueItem? _item;
    private MediaSource? _source;
    private ResolvedStream? _stream;
    private CancellationTokenSource? _loadCts;
    private long _loadId;
    private LoadKind _loadKind;
    private bool _autoplay;      // the user wants this item playing
    private TimeSpan _startAt;   // where to start once the media opens
    private bool _opened;        // MediaOpened arrived for the current load
    private bool _starting;      // Play() issued after opening; ignore the transient Paused state
    private bool _ended;         // MediaEnded arrived; ignore the Paused state that follows
    private bool _started;       // TrackStarted raised for the current item
    private bool _reopened;      // the one reopen of the same URL after a media failure is used up
    private bool _retried;       // the one re-resolve after a media failure is used up
    private int _consecutiveFailures;
    private Guid? _prefetchedFor;
    private double _volume;
    private bool _muted;
    private bool _volumeDirty;
    private bool _normalize;     // last seen AppSettings.NormalizeVolume
    private bool _pauseAtEnd;    // sleep timer: the next natural track end pauses instead of playing on
    private long _fadeId;
    private bool _disposed;
    private int _tick;

    public MediaPlayerService(
        IQueueService queue,
        IStreamResolver resolver,
        ISettingsService settings,
        VolumeNormalizer normalizer,
        IRadioNowPlaying radio,
        ILogger<MediaPlayerService> logger)
    {
        _queue = queue;
        _resolver = resolver;
        _settings = settings;
        _normalizer = normalizer;
        _radio = radio;
        _logger = logger;

        _volume = Math.Clamp(settings.Current.Volume, 0, 1);
        _normalize = settings.Current.NormalizeVolume;
        _player = new MediaPlayer
        {
            AudioCategory = MediaPlayerAudioCategory.Media,
            AutoPlay = false,
            Volume = _volume,
        };
        _mixer = new VolumeMixer(_volume, ApplyPlayerVolume);
        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        _smtc = new SmtcController(_player, this, logger);

        _positionTimer = new Timer(_ => OnPositionTick());
        _volumeSaveTimer = new Timer(_ => SaveVolume());
        _ = Task.Run(PumpEventsAsync);

        _queue.CurrentChanged += OnQueueCurrentChanged;
        _queue.Changed += OnQueueChanged;
        _settings.Changed += OnSettingsChanged;
        _radio.Changed += OnRadioNowPlayingChanged;
    }

    public event EventHandler<PlaybackStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;

    public event EventHandler<TrackChangedEventArgs>? TrackStarted;

    public event EventHandler<TrackChangedEventArgs>? TrackCompleted;

    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    public event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed;

    private enum LoadKind
    {
        User,
        AutoAdvance,

        /// <summary>Open the same (still cached) URL again after a network-level media failure.</summary>
        Reopen,

        /// <summary>Re-resolve after a media failure (uses up the one retry).</summary>
        Retry,

        /// <summary>Re-resolve because the URL is about to expire.</summary>
        Refresh,

        /// <summary>Live radio: connect to the same stream again (after a drop, or Play after a pause). Keeps the item's state.</summary>
        Reconnect,
    }

    public PlaybackStatus Status
    {
        get
        {
            lock (_gate)
            {
                return _status;
            }
        }
    }

    public Track? CurrentTrack
    {
        get
        {
            lock (_gate)
            {
                return _item?.Track;
            }
        }
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                return PositionNoLock();
            }
        }
    }

    public TimeSpan Duration
    {
        get
        {
            lock (_gate)
            {
                return DurationNoLock();
            }
        }
    }

    public double Volume
    {
        get
        {
            lock (_gate)
            {
                return _volume;
            }
        }

        set
        {
            if (double.IsNaN(value))
            {
                return;
            }

            lock (_gate)
            {
                value = Math.Clamp(value, 0, 1);
                if (_disposed || _volume == value)
                {
                    return;
                }

                _volume = value;
                _mixer.Volume = value;
                _volumeDirty = true;
                _volumeSaveTimer.Change(VolumeSaveDelay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    public bool IsMuted
    {
        get
        {
            lock (_gate)
            {
                return _muted;
            }
        }

        set
        {
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }

                _muted = value;
                _player.IsMuted = value;
            }
        }
    }

    public bool IsShuffleEnabled
    {
        get => _queue.IsShuffled;
        set => _queue.SetShuffle(value);
    }

    public RepeatMode RepeatMode
    {
        get => _queue.RepeatMode;
        set => _queue.RepeatMode = value;
    }

    public bool PauseAtEndOfTrack
    {
        get
        {
            lock (_gate)
            {
                return _pauseAtEnd;
            }
        }

        set
        {
            lock (_gate)
            {
                _pauseAtEnd = value;
            }
        }
    }

    public async Task FadeOutAndPauseAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        long fadeId;
        bool fade;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            // Each fade takes over the fade gain; an older fade's restore is then skipped (see RestoreFade).
            fadeId = ++_fadeId;
            fade = duration > TimeSpan.Zero
                && (_status is PlaybackStatus.Playing or PlaybackStatus.Buffering || (_status == PlaybackStatus.Loading && _autoplay));
            _mixer.SetFadeGain(fade ? 0 : 1, fade ? duration : TimeSpan.Zero);
        }

        if (!fade)
        {
            Pause();
            return;
        }

        _logger.LogInformation("Fading out over {Duration}, then pausing", duration);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RestoreFade(fadeId, GainRampTime);
            throw;
        }

        Pause();

        // Let the audio output actually stop before the volume comes back, or the tail of its buffer is heard.
        await Task.Delay(PauseSettleTime, CancellationToken.None).ConfigureAwait(false);
        RestoreFade(fadeId, TimeSpan.Zero);
    }

    public Task PlayAsync(CancellationToken cancellationToken = default)
    {
        QueueItem? item = null;
        var startAt = TimeSpan.Zero;
        var kind = LoadKind.User;
        lock (_gate)
        {
            if (_disposed)
            {
                return Task.CompletedTask;
            }

            _autoplay = true;
            if (_item is not null && _status == PlaybackStatus.Loading)
            {
                return Task.CompletedTask; // MediaOpened will start playback
            }

            if (_item is { Track.IsLiveRadio: true } live)
            {
                if (_source is not null && _opened && _status is PlaybackStatus.Playing or PlaybackStatus.Buffering)
                {
                    return Task.CompletedTask;
                }

                // Paused, stopped or failed: connect again at the live edge (a paused stream would replay stale audio).
                if (_started)
                {
                    kind = LoadKind.Reconnect;
                    _livePolicy.ResetAttempts();
                }

                item = live;
            }
            else if (_item is not null && _source is null)
            {
                startAt = _startAt; // set by Seek while nothing was loaded (e.g. a restored session)
            }

            if (item is null && _item is not null && _source is not null && _opened)
            {
                if (_ended)
                {
                    RestartNoLock();
                    _started = false;
                    _starting = true;
                    _player.Play();
                    return Task.CompletedTask;
                }

                if (_stream is null || _stream.ExpiresAt - DateTimeOffset.UtcNow > RefreshBeforeExpiry)
                {
                    _player.Play();
                    return Task.CompletedTask;
                }

                // Paused long enough for the URL to expire: get a fresh one and continue from here.
                _resolver.Invalidate(_item.Track.VideoId);
                startAt = PositionNoLock();
                kind = LoadKind.Refresh;
            }

            item ??= _item;
        }

        item ??= _queue.Current;
        return item is null ? Task.CompletedTask : LoadAsync(item, autoplay: true, startAt, kind, cancellationToken);
    }

    public Task PlayQueueIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        QueueItem? item;
        using (MovingQueue())
        {
            item = _queue.MoveTo(index) ? _queue.Current : null;
        }

        if (item is null)
        {
            _logger.LogWarning("Ignoring play request for queue index {Index}: out of range", index);
            return Task.CompletedTask;
        }

        return LoadAsync(item, autoplay: true, TimeSpan.Zero, LoadKind.User, cancellationToken);
    }

    public void Pause()
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _autoplay = false;
            _starting = false;
            if (_item?.Track.IsLiveRadio == true)
            {
                if (_source is not null || _status is PlaybackStatus.Loading or PlaybackStatus.Buffering or PlaybackStatus.Playing)
                {
                    // Live: drop the connection (a paused live stream only buffers stale audio); Play reconnects.
                    cancel = ResetLoadNoLock();
                    SetStatusNoLock(PlaybackStatus.Paused);
                    Post(() => _radio.Unfollow(forget: false));
                }
            }
            else if (_source is not null && _opened)
            {
                _player.Pause();
            }
        }

        cancel?.Cancel();
    }

    public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default)
    {
        bool playing;
        lock (_gate)
        {
            playing = _status is PlaybackStatus.Playing or PlaybackStatus.Buffering || (_status == PlaybackStatus.Loading && _autoplay);
        }

        if (playing)
        {
            Pause();
            return Task.CompletedTask;
        }

        return PlayAsync(cancellationToken);
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_disposed || _item is null || _item.Track.IsLiveRadio)
            {
                return; // live streams can't seek
            }

            var duration = DurationNoLock();
            position = position < TimeSpan.Zero ? TimeSpan.Zero : duration > TimeSpan.Zero && position > duration ? duration : position;
            _logger.LogDebug("Seek {VideoId} to {Position} (was {Current})", _item.Track.VideoId, position, _opened ? PositionNoLock() : _startAt);
            if (_source is not null && _opened)
            {
                _player.PlaybackSession.Position = position;
                _ended = false;
            }
            else
            {
                _startAt = position;
            }

            Post(() =>
            {
                PositionChanged?.Invoke(this, new PositionChangedEventArgs(position, duration));
                _smtc.SetTimeline(position, duration);
            });
        }
    }

    public Task NextAsync(CancellationToken cancellationToken = default)
    {
        QueueItem? next;
        using (MovingQueue())
        {
            next = _queue.MoveNext(userInitiated: true);
        }

        return next is null ? Task.CompletedTask : LoadAsync(next, autoplay: true, TimeSpan.Zero, LoadKind.User, cancellationToken);
    }

    public Task PreviousAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // A live station has no start to go back to: Previous always means the previous item.
            var live = _item?.Track.IsLiveRadio == true;
            if (!live && _source is not null && _opened && PositionNoLock() > RestartThreshold)
            {
                RestartNoLock();
                return Task.CompletedTask;
            }

            if (!live && _item is not null && _source is null && _startAt > RestartThreshold)
            {
                // Not loaded yet but with a start position (restored session): rewind it, like a restart.
                SetPendingStartNoLock(TimeSpan.Zero);
                return Task.CompletedTask;
            }
        }

        QueueItem? previous;
        using (MovingQueue())
        {
            previous = _queue.MovePrevious();
        }

        if (previous is not null)
        {
            return LoadAsync(previous, autoplay: true, TimeSpan.Zero, LoadKind.User, cancellationToken);
        }

        // At the start of the queue: restart the current track (nothing to restart on a live station).
        lock (_gate)
        {
            if (_item?.Track.IsLiveRadio == true)
            {
                return Task.CompletedTask;
            }

            if (_source is not null && _opened)
            {
                RestartNoLock();
                return Task.CompletedTask;
            }

            if (_source is null)
            {
                _startAt = TimeSpan.Zero;
            }
        }

        return PlayAsync(cancellationToken);
    }

    public void Stop()
    {
        CancellationTokenSource? cancel;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            cancel = ResetLoadNoLock();
            _startAt = TimeSpan.Zero;
            var duration = DurationNoLock();
            SetStatusNoLock(PlaybackStatus.Idle);
            Post(() =>
            {
                _radio.Unfollow(forget: true);
                PositionChanged?.Invoke(this, new PositionChangedEventArgs(TimeSpan.Zero, duration));
            });
        }

        cancel?.Cancel();
    }

    public void Dispose()
    {
        CancellationTokenSource? cancel;
        bool saveVolume;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            cancel = ResetLoadNoLock();
            _disposed = true;
            saveVolume = _volumeDirty;
        }

        cancel?.Cancel();
        _queue.CurrentChanged -= OnQueueCurrentChanged;
        _queue.Changed -= OnQueueChanged;
        _settings.Changed -= OnSettingsChanged;
        _radio.Changed -= OnRadioNowPlayingChanged;
        _radio.Unfollow(forget: true);
        _positionTimer.Dispose();
        _volumeSaveTimer.Dispose();
        if (saveVolume)
        {
            SaveVolume();
        }

        _player.MediaOpened -= OnMediaOpened;
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;
        _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        _smtc.Dispose();
        _mixer.Dispose();
        _player.Dispose();
        _events.Writer.TryComplete();
    }

    private static QueueMove MovingQueue()
    {
        t_movingQueue = true;
        return default;
    }

    /// <summary>Which load a MediaPlayer event belongs to, so late events from a replaced source are ignored.</summary>
    private bool TryGetLoadId(MediaPlayer sender, out long loadId)
    {
        try
        {
            var current = sender.Source;
            if (current is null)
            {
                loadId = 0;
                return false;
            }

            if (current is MediaSource source && source.CustomProperties.TryGetValue(LoadIdKey, out var value) && value is long id)
            {
                loadId = id;
                return true;
            }
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidCastException)
        {
        }

        // Tag unreadable: attribute the event to the current load rather than drop it and stall playback.
        lock (_gate)
        {
            loadId = _loadId;
            return _source is not null;
        }
    }

    private static string Describe(MediaPlayerFailedEventArgs args)
    {
        // HTTP failures surface as HRESULT_FROM_HTTP (0x8019xxxx), whatever MediaPlayerError says.
        var hresult = (uint)(args.ExtendedErrorCode?.HResult ?? 0);
        if ((hresult & 0xFFFF0000) == 0x80190000)
        {
            return $"The stream server refused the request (HTTP {hresult & 0xFFFF}).";
        }

        return args.Error switch
        {
            MediaPlayerError.NetworkError => "A network error interrupted this track.",
            MediaPlayerError.DecodingError => "This track's audio could not be decoded.",
            MediaPlayerError.SourceNotSupported when hresult == 0xC00D36C4 => "This track's audio format is not supported.",
            MediaPlayerError.Aborted => "Playback of this track was aborted.",
            _ => "This track could not be played.",
        };
    }

    /// <summary>
    /// Makes <paramref name="item"/> the loaded item and starts resolving + opening it.
    /// The returned task completes when the media source is set (or the load failed or was superseded); it never faults.
    /// </summary>
    private Task LoadAsync(QueueItem item, bool autoplay, TimeSpan startAt, LoadKind kind, CancellationToken waitToken, long? onlyIfLoadId = null)
    {
        long loadId;
        CancellationTokenSource cts;
        CancellationTokenSource? previous;
        lock (_gate)
        {
            if (_disposed || (onlyIfLoadId is { } expected && expected != _loadId))
            {
                return Task.CompletedTask;
            }

            previous = ResetLoadNoLock();
            cts = new CancellationTokenSource();
            _loadCts = cts;
            loadId = _loadId;

            var itemChanged = _item?.Id != item.Id;
            _item = item;
            _autoplay = autoplay;
            _startAt = startAt;
            switch (kind)
            {
                case LoadKind.Reopen:
                    _reopened = true;
                    break;
                case LoadKind.Retry:
                    _retried = true;
                    break;
                case LoadKind.Refresh:
                case LoadKind.Reconnect:
                    break;
                default:
                    _loadKind = kind;
                    _started = false;
                    _reopened = false;
                    _retried = false;
                    _prefetchedFor = null;
                    _livePolicy.Reset();
                    if (kind == LoadKind.User)
                    {
                        _consecutiveFailures = 0;
                    }

                    StartLoudnessNoLock(item);
                    break;
            }

            if (itemChanged)
            {
                Post(() =>
                {
                    _radio.Unfollow(forget: true);
                    _smtc.SetTrack(item.Track);
                    TrackChanged?.Invoke(this, new TrackChangedEventArgs(item.Track, item));
                });
            }

            SetStatusNoLock(PlaybackStatus.Loading);
        }

        // Start (or join) the new resolve before cancelling the old one, so a shared yt-dlp run is not killed in between.
        var work = ResolveAndOpenAsync(item, loadId, cts.Token);
        previous?.Cancel();
        return waitToken.CanBeCanceled ? work.WaitAsync(waitToken) : work;
    }

    private async Task ResolveAndOpenAsync(QueueItem item, long loadId, CancellationToken cancellationToken)
    {
        ResolvedStream stream;
        try
        {
            stream = await _resolver.ResolveAsync(item.Track, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // superseded
        }
        catch (Exception ex)
        {
            Fail(loadId, item, ex is HushException ? ex.Message : "Couldn't get an audio stream for this track.", ex);
            return;
        }

        try
        {
            await WarmUpHostAsync(stream.Url, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return; // superseded
        }

        try
        {
            lock (_gate)
            {
                if (loadId != _loadId || _disposed)
                {
                    return;
                }

                var source = MediaSource.CreateFromUri(stream.Url);
                source.CustomProperties[LoadIdKey] = loadId;
                _stream = stream;
                _source = source;
                _player.Source = source;
            }

            if (stream.IsLive)
            {
                _logger.LogInformation("Connecting to live station {Station} ({Codec}, {Bitrate} kbps): {Url}", item.Track.Title, stream.Codec, stream.BitrateKbps, stream.Url);
            }
            else
            {
                _logger.LogDebug("Opening {VideoId}: {Container}/{Codec} {Bitrate} kbps", item.Track.VideoId, stream.Container, stream.Codec, stream.BitrateKbps);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
        {
            Fail(loadId, item, "This track could not be opened.", ex);
        }
    }

    // Media Foundation's first lookup of a fresh googlevideo host sometimes fails ("server name could not be
    // resolved", 0xC00D2EE7). Resolving it here first puts the answer in the system DNS cache. Failures are ignored.
    private async Task WarmUpHostAsync(Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DnsWarmUpTimeout);
        try
        {
            await Dns.GetHostAddressesAsync(url.DnsSafeHost, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            _logger.LogDebug("DNS warm-up for {Host} failed: {Error}", url.Host, ex.Message);
        }
    }

    private void Fail(long loadId, QueueItem item, string message, Exception? exception)
    {
        bool skip;
        lock (_gate)
        {
            if (loadId != _loadId || _disposed)
            {
                return;
            }

            DetachSourceNoLock();
            _consecutiveFailures++;
            skip = _loadKind == LoadKind.AutoAdvance && _consecutiveFailures < MaxConsecutiveFailures;
            SetStatusNoLock(PlaybackStatus.Failed);
            Post(() => PlaybackFailed?.Invoke(this, new PlaybackErrorEventArgs(item.Track, message, exception)));
        }

        _logger.LogWarning(exception, "Playback failed for {VideoId}: {Message}", item.Track.VideoId, message);
        if (!skip)
        {
            return;
        }

        // An automatic advance hit an unplayable track: move on, like YouTube Music does (bounded, see MaxConsecutiveFailures).
        QueueItem? next;
        using (MovingQueue())
        {
            next = _queue.MoveNext(userInitiated: true);
        }

        if (next is not null && next.Id != item.Id)
        {
            _ = LoadAsync(next, autoplay: true, TimeSpan.Zero, LoadKind.AutoAdvance, CancellationToken.None);
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        if (!TryGetLoadId(sender, out var loadId))
        {
            return;
        }

        lock (_gate)
        {
            if (loadId != _loadId || _opened || _disposed)
            {
                return;
            }

            _opened = true;
            if (_startAt > TimeSpan.Zero)
            {
                sender.PlaybackSession.Position = _startAt;
                _logger.LogDebug("Opened {VideoId}, starting at {Position}", _item?.Track.VideoId, _startAt);
            }

            if (_autoplay)
            {
                _starting = true;
                sender.Play();
            }
            else
            {
                SetStatusNoLock(PlaybackStatus.Paused);
            }

            var position = _startAt;
            var duration = DurationNoLock();
            Post(() => _smtc.SetTimeline(position, duration));
        }
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession session, object args)
    {
        MediaPlaybackState state;
        try
        {
            state = session.PlaybackState;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || _source is null || !_opened)
            {
                return;
            }

            if (state == MediaPlaybackState.Paused && _autoplay && !_starting && _item?.Track.IsLiveRadio == true)
            {
                // We never pause a live stream ourselves (pausing disconnects): this is a drop (MediaEnded follows and
                // reconnects) or the system pausing (e.g. the audio device went away), confirmed after a moment.
                var loadId = _loadId;
                _logger.LogDebug("Live station {Station} reports paused; waiting to see whether the stream ended", _item.Track.Title);
                _ = ConfirmLivePauseAsync(loadId);
                return;
            }

            PlaybackStatus? status = state switch
            {
                MediaPlaybackState.Playing => PlaybackStatus.Playing,
                MediaPlaybackState.Paused when !_ended && !_starting && !ReachedEndNoLock() => PlaybackStatus.Paused,
                MediaPlaybackState.Buffering => PlaybackStatus.Buffering,
                MediaPlaybackState.Opening => PlaybackStatus.Loading,
                _ => null,
            };
            if (status is null)
            {
                return;
            }

            if (status == PlaybackStatus.Playing)
            {
                _starting = false;
                _autoplay = true;
                if (_item?.Track.Station is { } station)
                {
                    _livePolicy.OnPlaying();
                    Post(() => _radio.Follow(station));
                }
            }
            else if (status == PlaybackStatus.Paused)
            {
                _autoplay = false; // also covers pauses we did not ask for (e.g. the audio device went away)
                if (_item?.Track.IsLiveRadio == true)
                {
                    Post(() => _radio.Unfollow(forget: false));
                }
            }

            SetStatusNoLock(status.Value);
            if (status == PlaybackStatus.Playing && !_started && _item is { } item)
            {
                _started = true;
                _consecutiveFailures = 0;
                Post(() => TrackStarted?.Invoke(this, new TrackChangedEventArgs(item.Track, item)));
                Post(PrefetchNext);
            }
        }
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (!TryGetLoadId(sender, out var loadId))
        {
            return;
        }

        QueueItem item;
        bool pause;
        lock (_gate)
        {
            if (loadId != _loadId || _item is null || _disposed)
            {
                return;
            }

            item = _item;
            if (item.Track.IsLiveRadio)
            {
                // A live stream has no end: the server closed the connection. Reconnect rather than move on.
                _ = Task.Run(() => RecoverLive(item, loadId, fatal: false, "ended", "The station stopped sending audio.", null));
                return;
            }

            _ended = true;

            // Consumed here, before TrackCompleted is queued: a handler that sees the flag cleared knows it applied to this end.
            pause = _pauseAtEnd;
            _pauseAtEnd = false;
            Post(() => TrackCompleted?.Invoke(this, new TrackChangedEventArgs(item.Track, item)));
        }

        // Leave MediaPlayer's callback before touching its source again.
        _ = Task.Run(() => AdvanceAfterEnd(item, loadId, pause));
    }

    private void AdvanceAfterEnd(QueueItem finished, long loadId, bool pause)
    {
        lock (_gate)
        {
            if (loadId != _loadId || _disposed)
            {
                return;
            }
        }

        QueueItem? next;
        using (MovingQueue())
        {
            next = _queue.MoveNext(userInitiated: false);
        }

        if (next is null)
        {
            lock (_gate)
            {
                if (loadId == _loadId && !_disposed)
                {
                    _autoplay = false;
                    SetStatusNoLock(PlaybackStatus.Ended);
                }
            }

            return;
        }

        if (pause)
        {
            PauseAfterEnd(finished, next, loadId);
            return;
        }

        if (next.Id == finished.Id)
        {
            // RepeatMode.One (or a one-item queue on repeat): replay the open source, no re-resolve needed.
            lock (_gate)
            {
                if (loadId == _loadId && _source is not null && !_disposed)
                {
                    RestartNoLock();
                    _started = false;
                    _starting = true;
                    _player.Play();
                }
            }

            return;
        }

        _ = LoadAsync(next, autoplay: true, TimeSpan.Zero, LoadKind.AutoAdvance, CancellationToken.None, onlyIfLoadId: loadId);
    }

    // PauseAtEndOfTrack: the queue has moved on, but the next item only becomes current, paused at its start
    // (resolved when the user presses play), like after a queue change while paused.
    private void PauseAfterEnd(QueueItem finished, QueueItem next, long loadId)
    {
        CancellationTokenSource? cancel = null;
        lock (_gate)
        {
            if (loadId != _loadId || _disposed)
            {
                return;
            }

            _autoplay = false;
            _starting = false;
            if (next.Id == finished.Id)
            {
                if (_source is not null)
                {
                    RestartNoLock();
                }

                _started = false;
                SetStatusNoLock(PlaybackStatus.Paused);
            }
            else
            {
                cancel = ResetLoadNoLock();
                _item = next;
                _started = false;
                _startAt = TimeSpan.Zero;
                var duration = DurationNoLock();
                Post(() =>
                {
                    _radio.Unfollow(forget: true);
                    _smtc.SetTrack(next.Track);
                    TrackChanged?.Invoke(this, new TrackChangedEventArgs(next.Track, next));
                    PositionChanged?.Invoke(this, new PositionChangedEventArgs(TimeSpan.Zero, duration));
                });
                SetStatusNoLock(PlaybackStatus.Idle);
            }
        }

        cancel?.Cancel();
        _logger.LogInformation("Paused at the end of {VideoId} (sleep timer)", finished.Track.VideoId);
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        if (!TryGetLoadId(sender, out var loadId))
        {
            return;
        }

        var hresult = args.ExtendedErrorCode?.HResult ?? 0;
        QueueItem item;
        LoadKind? next;
        bool autoplay;
        TimeSpan resumeAt;
        lock (_gate)
        {
            if (loadId != _loadId || _item is null || _disposed)
            {
                return;
            }

            item = _item;
            if (item.Track.IsLiveRadio)
            {
                var fatal = IsRejectedUrl(hresult) || args.Error is MediaPlayerError.SourceNotSupported or MediaPlayerError.DecodingError;
                var reason = $"failed: {args.Error} \"{args.ErrorMessage}\" (0x{hresult:X8})";
                var message = DescribeLive(args, _livePolicy.HasPlayed);
                var error = args.ExtendedErrorCode;
                _ = Task.Run(() => RecoverLive(item, loadId, fatal, reason, message, error));
                return;
            }

            // Reopening costs ~0.3 s, re-resolving ~2.5 s (a yt-dlp run). Network hiccups such as a failed lookup of a
            // fresh googlevideo host (0xC00D2EE7) recover with the same URL; only a rejected URL needs a new one.
            next = !_reopened && !IsRejectedUrl(hresult) ? LoadKind.Reopen
                : !_retried ? LoadKind.Retry
                : null;
            autoplay = _autoplay;
            resumeAt = _opened ? PositionNoLock() : _startAt;
        }

        _logger.LogWarning(
            "MediaPlayer failed for {VideoId}: {Error} \"{Message}\" (0x{HResult:X8}); {Action}",
            item.Track.VideoId,
            args.Error,
            args.ErrorMessage,
            hresult,
            next switch
            {
                LoadKind.Reopen => "reopening the same URL",
                LoadKind.Retry => "re-resolving once",
                _ => "giving up",
            });

        if (next is not { } kind)
        {
            Fail(loadId, item, Describe(args), args.ExtendedErrorCode);
            return;
        }

        if (kind == LoadKind.Retry)
        {
            _resolver.Invalidate(item.Track.VideoId);
        }

        _ = Task.Run(() => LoadAsync(item, autoplay, resumeAt, kind, CancellationToken.None, onlyIfLoadId: loadId));
    }

    // Live radio: reconnect the same stream with a backoff (LiveReconnectPolicy), or give up and report it.
    private void RecoverLive(QueueItem item, long loadId, bool fatal, string reason, string message, Exception? exception)
    {
        TimeSpan? delay;
        bool autoplay;
        int attempt;
        lock (_gate)
        {
            if (loadId != _loadId || _disposed)
            {
                return;
            }

            autoplay = _autoplay;
            delay = autoplay ? _livePolicy.OnFailure(fatal) : null;
            attempt = _livePolicy.Attempts;
            if (!autoplay)
            {
                // Paused by the system (not by us, that would have disconnected): just let go of the dead connection.
                DetachSourceNoLock();
                SetStatusNoLock(PlaybackStatus.Paused);
                Post(() => _radio.Unfollow(forget: false));
                return;
            }

            if (delay is not null)
            {
                // Drop the dead connection now (its late events are ignored) and show buffering while waiting.
                DetachSourceNoLock();
                SetStatusNoLock(PlaybackStatus.Buffering);
            }
        }

        if (delay is not { } wait)
        {
            _logger.LogWarning("Live station {Station} {Reason}; giving up after {Attempts} reconnects", item.Track.Title, reason, Math.Max(0, attempt - 1));
            Post(() => _radio.Unfollow(forget: false));
            Fail(loadId, item, message, exception);
            return;
        }

        _logger.LogInformation("Live station {Station} {Reason}; reconnecting in {Delay} (attempt {Attempt})", item.Track.Title, reason, wait, attempt);
        _ = ReconnectLiveAsync(item, loadId, wait);
    }

    private async Task ConfirmLivePauseAsync(long loadId)
    {
        await Task.Delay(LivePauseConfirmTime).ConfigureAwait(false);
        lock (_gate)
        {
            // A drop has been handled meanwhile (RecoverLive detached the source), or anything else moved on.
            if (loadId != _loadId || _disposed || _source is null || !_opened || !_autoplay)
            {
                return;
            }

            MediaPlaybackState state;
            try
            {
                state = _player.PlaybackSession.PlaybackState;
            }
            catch (COMException)
            {
                return;
            }

            if (state != MediaPlaybackState.Paused)
            {
                return;
            }

            _logger.LogInformation("Live station {Station} was paused by the system", _item?.Track.Title);
            _autoplay = false;
            SetStatusNoLock(PlaybackStatus.Paused);
            Post(() => _radio.Unfollow(forget: false));
        }
    }

    private async Task ReconnectLiveAsync(QueueItem item, long loadId, TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);

        // Skipped when anything happened meanwhile (pause, another station, stop): the load id moved on.
        await LoadAsync(item, autoplay: true, TimeSpan.Zero, LoadKind.Reconnect, CancellationToken.None, onlyIfLoadId: loadId).ConfigureAwait(false);
    }

    private static string DescribeLive(MediaPlayerFailedEventArgs args, bool hasPlayed)
    {
        var hresult = (uint)(args.ExtendedErrorCode?.HResult ?? 0);
        if ((hresult & 0xFFFF0000) == 0x80190000)
        {
            return $"The station's server refused the connection (HTTP {hresult & 0xFFFF}).";
        }

        return hresult switch
        {
            // NS_E_FILE_NOT_FOUND: what an HTTP 404 from a streaming server surfaces as.
            0xC00D001A => "The station's stream wasn't found. It may have moved or gone off the air.",
            // MF_E_UNSUPPORTED_BYTESTREAM_TYPE, MF_E_UNSUPPORTED_FORMAT
            0xC00D36C4 or 0xC00D36C3 => "This station's audio format isn't supported.",
            _ when args.Error == MediaPlayerError.DecodingError => "This station's audio format isn't supported.",
            _ when hasPlayed => "The station's stream keeps dropping. Try again later.",
            _ => "Couldn't connect to this station.",
        };
    }

    // HTTP_E_STATUS_* (0x8019xxxx, low word = HTTP status): the URL itself was refused (expired, wrong IP, gone).
    private static bool IsRejectedUrl(int hresult) =>
        ((uint)hresult & 0xFFFF0000u) == 0x80190000u && (hresult & 0xFFFF) is 401 or 403 or 404 or 410;

    private void OnQueueCurrentChanged(object? sender, QueueCurrentChangedEventArgs e)
    {
        if (t_movingQueue)
        {
            return;
        }

        var current = e.Current;
        CancellationTokenSource? cancel = null;
        bool play;
        lock (_gate)
        {
            if (_disposed || current?.Id == _item?.Id)
            {
                return; // only the index moved
            }

            play = current is not null && _autoplay && _status is PlaybackStatus.Loading or PlaybackStatus.Playing or PlaybackStatus.Buffering;
            if (!play)
            {
                // Show the new current item (or nothing); it is resolved when the user presses play.
                cancel = ResetLoadNoLock();
                _item = current;
                _started = false;
                _startAt = TimeSpan.Zero;
                Post(() =>
                {
                    _radio.Unfollow(forget: true);
                    _smtc.SetTrack(current?.Track);
                    _smtc.SetStatus(PlaybackStatus.Idle, current is not null);
                    TrackChanged?.Invoke(this, new TrackChangedEventArgs(current?.Track, current));
                });
                SetStatusNoLock(PlaybackStatus.Idle);
            }
        }

        cancel?.Cancel();
        if (play)
        {
            _ = LoadAsync(current!, autoplay: true, TimeSpan.Zero, LoadKind.User, CancellationToken.None);
        }
    }

    // Raised by the ICY reader (on its thread or on our event pump): show the song in the system media controls.
    private void OnRadioNowPlayingChanged(object? sender, RadioNowPlayingChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_disposed || _item?.Track is not { Station: { } station } track || station.Id != e.StationId)
            {
                return;
            }

            var nowPlaying = e.NowPlaying;
            Post(() => _smtc.SetTrack(track, nowPlaying));
        }
    }

    private void OnQueueChanged(object? sender, QueueChangedEventArgs e)
    {
        // The queue may have grown after the track started (e.g. "up next" arrived): prefetch what now comes next.
        Post(PrefetchNext);
    }

    private void PrefetchNext()
    {
        var next = _queue.PeekNext();
        bool normalize;
        lock (_gate)
        {
            if (!_started || next is null || next.Id == _item?.Id || next.Id == _prefetchedFor || next.Track.IsLiveRadio)
            {
                return;
            }

            _prefetchedFor = next.Id;
            normalize = _normalize;
        }

        _resolver.Prefetch(next.Track.VideoId);
        if (normalize)
        {
            _normalizer.Prefetch(next.Track.VideoId);
        }
    }

    // A new item is loading and nothing is audible yet: apply its known gain at once, or full gain until its loudness
    // arrives (fetched alongside the stream, so playback never waits for it).
    private void StartLoudnessNoLock(QueueItem item)
    {
        if (!_normalize || item.Track.IsLiveRadio)
        {
            _mixer.SetLoudnessGain(1, TimeSpan.Zero);
            return;
        }

        if (_normalizer.TryGetLoudness(item.Track.VideoId, out var loudnessDb))
        {
            var gain = PlaybackGain.ForLoudness(loudnessDb);
            _mixer.SetLoudnessGain(gain, TimeSpan.Zero);
            LogGain(item, loudnessDb, gain, "cached");
            return;
        }

        _mixer.SetLoudnessGain(1, TimeSpan.Zero);
        _ = Task.Run(() => ApplyLoudnessWhenKnownAsync(item));
    }

    private async Task ApplyLoudnessWhenKnownAsync(QueueItem item)
    {
        var loudnessDb = await _normalizer.GetLoudnessDbAsync(item.Track.VideoId).ConfigureAwait(false);
        var gain = PlaybackGain.ForLoudness(loudnessDb);
        bool audible;
        lock (_gate)
        {
            if (_disposed || !_normalize || _item?.Id != item.Id)
            {
                return;
            }

            audible = _opened && _status is PlaybackStatus.Playing or PlaybackStatus.Buffering;
            _mixer.SetLoudnessGain(gain, audible ? GainRampTime : TimeSpan.Zero);
        }

        LogGain(item, loudnessDb, gain, audible ? "ramped, audio had started" : "before audio");
    }

    private void LogGain(QueueItem item, double? loudnessDb, double gain, string how) =>
        _logger.LogDebug(
            "Normalization gain for {VideoId}: {Gain} (loudness {LoudnessDb} dB, {How})",
            item.Track.VideoId,
            gain.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture),
            loudnessDb?.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) ?? "unknown",
            how);

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        bool enabled;
        QueueItem? fetchFor = null;
        lock (_gate)
        {
            enabled = _settings.Current.NormalizeVolume;
            if (_disposed || enabled == _normalize)
            {
                return;
            }

            _normalize = enabled;
            if (!enabled)
            {
                _mixer.SetLoudnessGain(1, GainRampTime);
            }
            else if (_item is { } item)
            {
                if (_normalizer.TryGetLoudness(item.Track.VideoId, out var loudnessDb))
                {
                    _mixer.SetLoudnessGain(PlaybackGain.ForLoudness(loudnessDb), GainRampTime);
                }
                else
                {
                    fetchFor = item;
                }
            }
        }

        _logger.LogInformation("Volume normalization turned {State}", enabled ? "on" : "off");
        if (fetchFor is not null)
        {
            _ = Task.Run(() => ApplyLoudnessWhenKnownAsync(fetchFor));
        }
    }

    private void RestoreFade(long fadeId, TimeSpan ramp)
    {
        lock (_gate)
        {
            // Skipped when a newer fade owns the gain now.
            if (fadeId == _fadeId && !_disposed)
            {
                _mixer.SetFadeGain(1, ramp);
            }
        }
    }

    private void ApplyPlayerVolume(double volume)
    {
        try
        {
            _player.Volume = volume;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not set the MediaPlayer volume");
        }
    }

    private void OnPositionTick()
    {
        TimeSpan position;
        TimeSpan duration;
        lock (_gate)
        {
            if (_disposed || _status != PlaybackStatus.Playing)
            {
                return;
            }

            position = PositionNoLock();
            duration = DurationNoLock();
        }

        var updateTimeline = Interlocked.Increment(ref _tick) % 4 == 0;
        Post(() =>
        {
            PositionChanged?.Invoke(this, new PositionChangedEventArgs(position, duration));
            if (updateTimeline)
            {
                _smtc.SetTimeline(position, duration);
            }
        });
    }

    private void SaveVolume()
    {
        double volume;
        lock (_gate)
        {
            if (!_volumeDirty)
            {
                return;
            }

            _volumeDirty = false;
            volume = _volume;
        }

        _ = SaveVolumeAsync(volume);
    }

    private async Task SaveVolumeAsync(double volume)
    {
        try
        {
            await _settings.UpdateAsync(s => s.Volume = volume).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the volume");
        }
    }

    private async Task PumpEventsAsync()
    {
        await foreach (var action in _events.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "A player event handler failed");
            }
        }
    }

    // Called under _gate so events keep the order of the state changes that caused them.
    private void Post(Action action) => _events.Writer.TryWrite(action);

    private void SetStatusNoLock(PlaybackStatus status)
    {
        if (_status == status)
        {
            return;
        }

        _status = status;
        var hasTrack = _item is not null;
        Post(() =>
        {
            _smtc.SetStatus(status, hasTrack);
            StatusChanged?.Invoke(this, new PlaybackStatusChangedEventArgs(status));
        });

        if (!_disposed)
        {
            _positionTimer.Change(status == PlaybackStatus.Playing ? PositionInterval : Timeout.InfiniteTimeSpan, PositionInterval);
        }
    }

    /// <summary>Invalidates the current load and closes its source. Returns the old token source; cancel it after leaving the lock.</summary>
    private CancellationTokenSource? ResetLoadNoLock()
    {
        var previous = _loadCts;
        _loadCts = null;
        _loadId++;
        _opened = false;
        _starting = false;
        _ended = false;
        DetachSourceNoLock();
        return previous;
    }

    private void DetachSourceNoLock()
    {
        var source = _source;
        _source = null;
        _stream = null;
        _opened = false;
        if (source is null)
        {
            return;
        }

        try
        {
            _player.Source = null;
            source.Dispose();
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Closing the previous media source failed");
        }
    }

    private void SetPendingStartNoLock(TimeSpan position)
    {
        _startAt = position;
        var duration = DurationNoLock();
        Post(() =>
        {
            PositionChanged?.Invoke(this, new PositionChangedEventArgs(position, duration));
            _smtc.SetTimeline(position, duration);
        });
    }

    private void RestartNoLock()
    {
        _ended = false;
        _player.PlaybackSession.Position = TimeSpan.Zero;
        var duration = DurationNoLock();
        Post(() =>
        {
            PositionChanged?.Invoke(this, new PositionChangedEventArgs(TimeSpan.Zero, duration));
            _smtc.SetTimeline(TimeSpan.Zero, duration);
        });
    }

    // MediaPlayer reports Paused just before MediaEnded; treating that as a pause would flicker the play button.
    private bool ReachedEndNoLock()
    {
        var duration = DurationNoLock();
        return _autoplay && duration > TimeSpan.Zero && PositionNoLock() >= duration - TimeSpan.FromMilliseconds(500);
    }

    private TimeSpan PositionNoLock()
    {
        if (_disposed || _source is null || !_opened)
        {
            return _startAt;
        }

        try
        {
            return _player.PlaybackSession.Position;
        }
        catch (COMException)
        {
            return _startAt;
        }
    }

    private TimeSpan DurationNoLock()
    {
        if (_item?.Track.IsLiveRadio == true)
        {
            return TimeSpan.Zero;
        }

        if (!_disposed && _source is not null && _opened)
        {
            try
            {
                var natural = _player.PlaybackSession.NaturalDuration;
                if (natural > TimeSpan.Zero)
                {
                    return natural;
                }
            }
            catch (COMException)
            {
            }
        }

        return _stream?.Duration ?? _item?.Track.Duration ?? TimeSpan.Zero;
    }

    private readonly struct QueueMove : IDisposable
    {
        public void Dispose() => t_movingQueue = false;
    }
}
