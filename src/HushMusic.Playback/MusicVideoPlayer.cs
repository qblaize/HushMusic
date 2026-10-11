using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Media.Core;
using Windows.Media.Playback;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Playback;

/// <summary>What a view showing <see cref="MusicVideoPlayer"/> should show.</summary>
public enum MusicVideoState
{
    /// <summary>Not active: nothing is fetched or decoded.</summary>
    Off,

    /// <summary>The playing item has no picture of its own (a song, live radio, nothing playing): show the cover.</summary>
    NoVideo,

    /// <summary>Getting the picture ready: keep showing the cover.</summary>
    Loading,

    /// <summary>The picture is on screen and follows the sound.</summary>
    Showing,

    /// <summary>The picture couldn't be played for this item: show the cover.</summary>
    Failed,
}

/// <summary>
/// The picture of the playing music video, beside the sound: a second, muted <see cref="Windows.Media.Playback.MediaPlayer"/>
/// with a video-only stream (<see cref="IStreamResolver.ResolveVideoAsync"/>), kept in step with <see cref="IPlayer"/>
/// (<see cref="VideoSync"/>). The sound is never touched.
/// </summary>
/// <remarks>
/// It works only while a view shows it: <see cref="Activate"/> creates the player and fetches the current item's picture;
/// <see cref="Deactivate"/> stops and disposes it at once, so nothing is downloaded or decoded while nobody looks.
/// Activating again picks up where the sound is. Only items that are music videos themselves
/// (<see cref="Track.IsMusicVideo"/>) have a picture: a song's music video (<see cref="Track.VideoVersion"/>) is another
/// recording or edit, so it can't be shown over the song's sound.
/// <para>
/// A rejected stream URL (HTTP 401/403/404/410) is resolved again once, as for audio. A picture Windows can't decode
/// (no VP9 decoder) turns video off for the session. Events are raised on a background thread.
/// </para>
/// </remarks>
public sealed class MusicVideoPlayer : IDisposable
{
    private const string LoadIdKey = "HushMusic.VideoLoadId";
    private const int MaxSeeksOutOfStep = 4;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SeekTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan DnsWarmUpTimeout = TimeSpan.FromSeconds(2);

    private readonly object _gate = new();
    private readonly IPlayer _player;
    private readonly IStreamResolver _resolver;
    private readonly ILogger<MusicVideoPlayer> _logger;
    private readonly Timer _timer;

    // Guarded by _gate.
    private MediaPlayer? _video;

    // Held for the player's lifetime: events compare and unsubscribe through this very wrapper (a session wrapper
    // fetched again later can be a different object).
    private MediaPlaybackSession? _session;
    private Load? _load;
    private MusicVideoState _state = MusicVideoState.Off;
    private int _maxHeight = VideoFormats.DefaultMaxHeight;
    private long _ids;
    private TimeSpan _lead = VideoSync.InitialLead;
    private bool _unsupported;
    private bool _disposed;

    public MusicVideoPlayer(IPlayer player, IStreamResolver resolver, ILogger<MusicVideoPlayer> logger)
    {
        _player = player;
        _resolver = resolver;
        _logger = logger;
        _timer = new Timer(_ => Sync());
    }

    /// <summary>Raised on a background thread when <see cref="State"/> changes.</summary>
    public event EventHandler? StateChanged;

    public MusicVideoState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <summary>Width / height of the picture being shown, or null.</summary>
    public double? AspectRatio
    {
        get
        {
            lock (_gate)
            {
                return _load?.Stream is { Width: > 0, Height: > 0 } stream ? (double)stream.Width.Value / stream.Height.Value : null;
            }
        }
    }

    /// <summary>
    /// Starts showing the playing item's picture, at most <paramref name="maxHeight"/> pixels tall
    /// (<see cref="VideoFormats.HeightFor"/>), and returns the player to render (e.g. with a MediaPlayerElement). Calling
    /// it again while active returns the same player; a new size applies from the next item.
    /// </summary>
    public MediaPlayer Activate(int maxHeight)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _maxHeight = Math.Clamp(maxHeight, 144, VideoFormats.MaxHeight);
            if (_video is { } active)
            {
                return active;
            }

            _video = CreatePlayer();
            _session = _video.PlaybackSession;
            _session.SeekCompleted += OnSeekCompleted;
            _player.TrackChanged += OnTrackChanged;
            _player.StatusChanged += OnStatusChanged;
            _logger.LogDebug("Video on (up to {MaxHeight}p)", _maxHeight);
            StartLoadNoLock(_player.CurrentTrack);
            return _video;
        }
    }

    /// <summary>
    /// Stops at once: the download and decoding end and the player is disposed (detach it from the view first). The
    /// sound plays on.
    /// </summary>
    public void Deactivate()
    {
        MediaPlayer? video;
        MediaPlaybackSession? session;
        lock (_gate)
        {
            video = _video;
            session = _session;
            if (video is null || session is null)
            {
                return;
            }

            _player.TrackChanged -= OnTrackChanged;
            _player.StatusChanged -= OnStatusChanged;
            _video = null;
            _session = null;
            CancelLoadNoLock();
            _timer.Change(Timeout.Infinite, Timeout.Infinite);
            SetStateNoLock(MusicVideoState.Off);
        }

        ReleasePlayer(video, session);
        _logger.LogDebug("Video off");
    }

    public void Dispose()
    {
        Deactivate();
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private MediaPlayer CreatePlayer()
    {
        var video = new MediaPlayer
        {
            AutoPlay = false,
            IsMuted = true,
            Volume = 0,
        };

        // A second media session would show up in the system media controls; the picture has no say there.
        video.CommandManager.IsEnabled = false;
        video.MediaOpened += OnMediaOpened;
        video.MediaFailed += OnMediaFailed;
        video.MediaEnded += OnMediaEnded;
        return video;
    }

    private void ReleasePlayer(MediaPlayer video, MediaPlaybackSession session)
    {
        video.MediaOpened -= OnMediaOpened;
        video.MediaFailed -= OnMediaFailed;
        video.MediaEnded -= OnMediaEnded;
        try
        {
            session.SeekCompleted -= OnSeekCompleted;
            video.Pause();
            video.Source = null;
        }
        catch (Exception ex) when (ex is COMException or ObjectDisposedException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Releasing the video player");
        }

        video.Dispose();
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_video is not null && e.Track?.VideoId != _load?.Track.VideoId)
            {
                StartLoadNoLock(e.Track);
            }
        }
    }

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e) => Sync();

    private void StartLoadNoLock(Track? track)
    {
        CancelLoadNoLock();
        if (_video is not { } video)
        {
            return;
        }

        if (track is not { IsMusicVideo: true })
        {
            SetStateNoLock(MusicVideoState.NoVideo);
            return;
        }

        if (_unsupported)
        {
            SetStateNoLock(MusicVideoState.Failed);
            return;
        }

        var load = new Load(++_ids, track, _maxHeight);
        _load = load;
        SetStateNoLock(MusicVideoState.Loading);
        _ = ResolveAndOpenAsync(video, load);
    }

    private void CancelLoadNoLock()
    {
        if (_load is not { } load)
        {
            return;
        }

        _load = null;
        load.Cts.Cancel();
        if (_video is { } video && video.Source is not null)
        {
            try
            {
                video.Pause();
                video.Source = null;
            }
            catch (COMException ex)
            {
                _logger.LogDebug(ex, "Clearing the video source");
            }
        }
    }

    private async Task ResolveAndOpenAsync(MediaPlayer video, Load load)
    {
        var videoId = load.Track.VideoId;
        ResolvedStream stream;
        try
        {
            stream = await _resolver.ResolveVideoAsync(videoId, load.MaxHeight, load.Cts.Token).ConfigureAwait(false);
            await WarmUpHostAsync(stream.Url, load.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (load.Cts.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogInformation("No video for {VideoId}: {Message}", videoId, ex is HushException ? ex.Message : ex.GetType().Name);
            Fail(load);
            return;
        }

        lock (_gate)
        {
            if (_load != load || _video != video)
            {
                return;
            }

            try
            {
                var source = MediaSource.CreateFromUri(stream.Url);
                source.CustomProperties[LoadIdKey] = load.Id;
                load.Stream = stream;
                load.OpenStartedMs = Environment.TickCount64;
                video.Source = source;
            }
            catch (Exception ex) when (ex is COMException or ArgumentException or InvalidOperationException)
            {
                _logger.LogWarning(ex, "Could not open the video of {VideoId}", videoId);
                FailNoLock(load);
                return;
            }
        }

        _logger.LogDebug("Opening the video of {VideoId}: {Width}x{Height} {Codec}", videoId, stream.Width, stream.Height, stream.Codec);
    }

    // As for audio: Media Foundation's first lookup of a fresh googlevideo host sometimes fails. Failures are ignored.
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

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        lock (_gate)
        {
            if (CurrentLoadNoLock(sender) is not { } load)
            {
                return;
            }

            load.Opened = true;
            _logger.LogDebug("Video of {VideoId} opened in {Elapsed} ms", load.Track.VideoId, Environment.TickCount64 - load.OpenStartedMs);
        }

        Sync();
    }

    // Some videos end a moment before their sound: the last frame stays up (see Sync).
    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        lock (_gate)
        {
            if (CurrentLoadNoLock(sender) is { } load)
            {
                load.Ended = true;
                load.Playing = false;
            }
        }
    }

    private void OnSeekCompleted(MediaPlaybackSession session, object args)
    {
        lock (_gate)
        {
            if (_load is not { Seeking: true } load || _session is null)
            {
                return;
            }

            var took = TimeSpan.FromMilliseconds(Environment.TickCount64 - load.SeekStartedMs);
            load.Seeking = false;
            if (load.SeekWhilePlaying)
            {
                _lead = VideoSync.NextLead(_lead, took);
            }

            _logger.LogDebug("Video seek took {Elapsed} ms; lead now {Lead} ms", (long)took.TotalMilliseconds, (long)_lead.TotalMilliseconds);
            if (_state == MusicVideoState.Loading)
            {
                SetStateNoLock(MusicVideoState.Showing);
            }
        }

        Sync();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        var hresult = args.ExtendedErrorCode?.HResult ?? 0;
        lock (_gate)
        {
            if (CurrentLoadNoLock(sender) is not { } load)
            {
                return;
            }

            _logger.LogInformation(
                "Video of {VideoId} failed: {Error} \"{Message}\" (0x{HResult:X8})",
                load.Track.VideoId,
                args.Error,
                args.ErrorMessage,
                hresult);

            if (IsRejectedUrl(hresult) && !load.Retried)
            {
                // Expired or refused link: resolve once more and pick up where the sound is.
                _resolver.Invalidate(load.Track.VideoId);
                CancelLoadNoLock();
                var retry = new Load(++_ids, load.Track, load.MaxHeight) { Retried = true };
                _load = retry;
                SetStateNoLock(MusicVideoState.Loading);
                _ = ResolveAndOpenAsync(sender, retry);
                return;
            }

            if (args.Error == MediaPlayerError.DecodingError || IsMissingDecoder(hresult))
            {
                // No VP9 decoder on this PC (it ships with Windows as the "VP9 Video Extensions" app): don't keep trying.
                _unsupported = true;
                _logger.LogWarning("Videos can't be shown: this PC can't decode the video format (0x{HResult:X8})", hresult);
            }

            FailNoLock(load);
        }
    }

    private void Fail(Load load)
    {
        lock (_gate)
        {
            FailNoLock(load);
        }
    }

    private void FailNoLock(Load load)
    {
        if (_load != load)
        {
            return;
        }

        CancelLoadNoLock();
        SetStateNoLock(MusicVideoState.Failed);
    }

    // The load a media event belongs to, when it is still the current one.
    private Load? CurrentLoadNoLock(MediaPlayer sender) =>
        _load is { } load && sender == _video && sender.Source is MediaSource source
        && source.CustomProperties.TryGetValue(LoadIdKey, out var id) && id is long loadId && loadId == load.Id
            ? load
            : null;

    /// <summary>One step of keeping the picture with the sound. Runs on events and every <see cref="TickInterval"/>.</summary>
    private void Sync()
    {
        // The player's own state first, outside our lock (it takes its own).
        var status = _player.Status;
        var audio = _player.Position;
        var current = _player.CurrentTrack?.VideoId;
        var audioPlaying = status == PlaybackStatus.Playing;

        lock (_gate)
        {
            if (_disposed || _video is not { } video || _session is not { } session || _load is not { Opened: true } load)
            {
                return;
            }

            if (current != load.Track.VideoId)
            {
                Schedule(TickInterval); // TrackChanged is on its way
                return;
            }

            if (load.Seeking)
            {
                if (Environment.TickCount64 - load.SeekStartedMs < SeekTimeout.TotalMilliseconds)
                {
                    Schedule(TickInterval);
                    return;
                }

                _logger.LogDebug("Video seek of {VideoId} didn't complete; going on", load.Track.VideoId);
                load.Seeking = false;
            }

            TimeSpan position, length;
            try
            {
                position = session.Position;
                length = session.NaturalDuration;
            }
            catch (COMException)
            {
                Schedule(TickInterval);
                return;
            }

            VideoSyncDecision decision;
            if (!load.FirstFrameAsked)
            {
                // The first seek puts a frame on screen even while the sound is paused; Showing follows when it completes.
                load.FirstFrameAsked = true;
                decision = VideoSyncDecision.SeekTo(audioPlaying ? audio + _lead : audio);
            }
            else if (load.Ended && length > TimeSpan.Zero && audio >= length - VideoSync.SeekThreshold)
            {
                // The picture ended a moment before the sound: keep its last frame.
                decision = VideoSyncDecision.None;
            }
            else
            {
                load.Ended = false;
                decision = VideoSync.Decide(audioPlaying, audio, position, load.Playing, _lead);
            }

            Apply(video, session, load, decision, audioPlaying, audio - position);
        }
    }

    private void Apply(MediaPlayer video, MediaPlaybackSession session, Load load, VideoSyncDecision decision, bool audioPlaying, TimeSpan drift)
    {
        var next = TickInterval;
        try
        {
            switch (decision.Action)
            {
                case VideoSyncAction.Pause:
                    video.Pause();
                    load.Playing = false;
                    break;
                case VideoSyncAction.Seek:
                    if (audioPlaying && ++load.SeeksOutOfStep > MaxSeeksOutOfStep)
                    {
                        _logger.LogInformation("The video of {VideoId} can't keep up with the sound; showing the cover", load.Track.VideoId);
                        FailNoLock(load);
                        return;
                    }

                    video.Pause();
                    load.Playing = false;
                    load.Seeking = true;
                    load.SeekWhilePlaying = audioPlaying;
                    load.SeekStartedMs = Environment.TickCount64;
                    if (audioPlaying && load.OutOfStepSinceMs == 0)
                    {
                        load.OutOfStepSinceMs = load.SeekStartedMs;
                    }

                    session.PlaybackRate = 1;
                    session.Position = ClampToDuration(load, decision.Target);
                    _logger.LogDebug("Video {Drift} ms off; seeking to {Target}", (long)drift.TotalMilliseconds, decision.Target);
                    break;
                case VideoSyncAction.Hold:
                    next = decision.Delay < TickInterval ? decision.Delay : TickInterval;
                    break;
                case VideoSyncAction.Play:
                    if (Math.Abs(session.PlaybackRate - decision.Rate) > 0.005)
                    {
                        session.PlaybackRate = decision.Rate;
                    }

                    if (!load.Playing)
                    {
                        video.Play();
                        load.Playing = true;
                    }

                    if (decision.Rate == 1)
                    {
                        load.SeeksOutOfStep = 0;
                        if (load.OutOfStepSinceMs != 0)
                        {
                            _logger.LogDebug(
                                "Video of {VideoId} in step again after {Elapsed} ms ({Drift} ms off)",
                                load.Track.VideoId,
                                Environment.TickCount64 - load.OutOfStepSinceMs,
                                (long)drift.TotalMilliseconds);
                            load.OutOfStepSinceMs = 0;
                        }
                    }

                    if (_state == MusicVideoState.Loading)
                    {
                        SetStateNoLock(MusicVideoState.Showing);
                    }

                    break;
            }
        }
        catch (COMException ex)
        {
            _logger.LogDebug(ex, "Video sync step failed");
        }

        Schedule(next);
    }

    private static TimeSpan ClampToDuration(Load load, TimeSpan target)
    {
        var duration = load.Stream?.Duration ?? load.Track.Duration;
        return duration is { } length && target > length ? length : target;
    }

    private void Schedule(TimeSpan due) =>
        _timer.Change(due < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : due, Timeout.InfiniteTimeSpan);

    private void SetStateNoLock(MusicVideoState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        ThreadPool.QueueUserWorkItem(_ => StateChanged?.Invoke(this, EventArgs.Empty));
    }

    // HTTP_E_STATUS_* (0x8019xxxx, low word = HTTP status): the URL itself was refused (expired, wrong IP, gone).
    private static bool IsRejectedUrl(int hresult) =>
        ((uint)hresult & 0xFFFF0000u) == 0x80190000u && (hresult & 0xFFFF) is 401 or 403 or 404 or 410;

    // MF_E_TOPO_CODEC_NOT_FOUND: no decoder for the stream's format.
    private static bool IsMissingDecoder(int hresult) => (uint)hresult == 0xC00D5212;

    private sealed class Load(long id, Track track, int maxHeight)
    {
        public long Id { get; } = id;

        public Track Track { get; } = track;

        public int MaxHeight { get; } = maxHeight;

        public CancellationTokenSource Cts { get; } = new();

        public ResolvedStream? Stream { get; set; }

        public bool Retried { get; init; }

        public long OpenStartedMs { get; set; }

        public bool Opened { get; set; }

        public bool FirstFrameAsked { get; set; }

        /// <summary>The picture played to its end (MediaEnded).</summary>
        public bool Ended { get; set; }

        /// <summary>The picture player was told to play (and not paused since).</summary>
        public bool Playing { get; set; }

        public bool Seeking { get; set; }

        public bool SeekWhilePlaying { get; set; }

        public long SeekStartedMs { get; set; }

        /// <summary>When the picture last lost step with the playing sound (0: in step).</summary>
        public long OutOfStepSinceMs { get; set; }

        /// <summary>Seeks since the picture last ran in step; too many and it gives up for this item.</summary>
        public int SeeksOutOfStep { get; set; }
    }
}
