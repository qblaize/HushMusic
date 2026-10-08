using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>
/// Transport, seek, volume and like state, shared by the player bar and Now Playing. All player/queue events are
/// marshalled to the UI thread. Singleton.
/// While a live radio station plays (<see cref="IsLive"/>), <see cref="Title"/> is the song it is playing (ICY metadata,
/// else the station name) and <see cref="Subtitle"/> is "Artist · Station"; there is no timeline and nothing to rate.
/// <see cref="IsMinimalLayout"/> follows the "Player layout" setting, live.
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject
{
    private static readonly TimeSpan VolumeSaveDelay = TimeSpan.FromMilliseconds(600);
    private static readonly TimeSpan SeekSettleTime = TimeSpan.FromMilliseconds(800);

    private readonly IPlayer _player;
    private readonly IQueueService _queue;
    private readonly IAuthService _auth;
    private readonly IAccountActionsService _accountActions;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly IRadioNowPlaying _radio;
    private readonly ILogger<PlayerViewModel> _logger;

    // Ratings made in this session win over the (possibly stale) LikeStatus carried by queued tracks.
    private readonly Dictionary<string, LikeStatus> _ratings = new(StringComparer.Ordinal);

    private CancellationTokenSource? _volumeSaveCts;
    private bool _syncing;
    private bool _isSeeking;
    private double _seekStartValue;
    private double _lastSeekTarget = -1;
    private DateTime _ignorePositionUntil;
    private RadioNowPlaying? _nowPlaying;

    public PlayerViewModel(
        IPlayer player,
        IQueueService queue,
        IAuthService auth,
        IAccountActionsService accountActions,
        ISettingsService settings,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        IRadioNowPlaying radio,
        ILogger<PlayerViewModel> logger)
    {
        _player = player;
        _queue = queue;
        _auth = auth;
        _accountActions = accountActions;
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _radio = radio;
        _logger = logger;

        _radio.Changed += (_, e) => _dispatcher.Run(() => OnRadioNowPlaying(e.StationId, e.NowPlaying));
        _player.TrackChanged += (_, e) => _dispatcher.Run(() => SetTrack(e.Track));
        _player.StatusChanged += (_, e) => _dispatcher.Run(() => Status = e.Status);
        _player.PositionChanged += (_, e) => _dispatcher.Run(() => UpdatePosition(e.Position, e.Duration));
        _player.PlaybackFailed += (_, e) => _dispatcher.Run(() => OnPlaybackFailed(e));
        _queue.Changed += (_, e) => _dispatcher.Run(() => OnQueueChanged(e.Kind));
        _auth.StatusChanged += (_, _) => _dispatcher.Run(() => OnPropertyChanged(nameof(CanRate)));
        _accountActions.TrackRated += (_, e) => _dispatcher.Run(() => OnTrackRated(e.VideoId, e.Status));

        IsMinimalLayout = PlayerLayouts.IsMinimal(_settings.Current.PlayerLayout);
        _settings.Changed += (_, _) => _dispatcher.Run(() => IsMinimalLayout = PlayerLayouts.IsMinimal(_settings.Current.PlayerLayout));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTrack), nameof(CanRate), nameof(Title), nameof(Subtitle), nameof(CoverUrl), nameof(IsLive), nameof(IsNotLive))]
    [NotifyPropertyChangedFor(nameof(LiveNowPlaying), nameof(LiveArtUrl), nameof(StationName), nameof(CanFindOnYouTubeMusic), nameof(TitleToolTip))]
    public partial Track? Track { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaying), nameof(IsLoading), nameof(PlayPauseGlyph), nameof(PlayPauseLabel))]
    public partial PlaybackStatus Status { get; set; }

    /// <summary>Seek slider value in seconds (two-way bound).</summary>
    [ObservableProperty]
    public partial double PositionSeconds { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSeek))]
    public partial double DurationSeconds { get; set; }

    [ObservableProperty]
    public partial string PositionText { get; set; } = "0:00";

    [ObservableProperty]
    public partial string DurationText { get; set; } = "0:00";

    /// <summary>"−1:23": time left in the track, shown on the right of the seek bar.</summary>
    [ObservableProperty]
    public partial string RemainingText { get; set; } = "0:00";

    /// <summary>0 – 100 (two-way bound to the volume slider).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph))]
    public partial double Volume { get; set; } = 80;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumeGlyph), nameof(MuteLabel))]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShuffleLabel))]
    public partial bool IsShuffleEnabled { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatActive), nameof(RepeatGlyph), nameof(RepeatLabel))]
    public partial RepeatMode Repeat { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeGlyph), nameof(LikeLabel))]
    public partial bool IsLiked { get; set; }

    /// <summary>
    /// The Minimal player layout: a docked bar with the rest in a menu, and a calmer Now Playing. Standard is the floating
    /// glass bar.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStandardLayout))]
    public partial bool IsMinimalLayout { get; set; }

    public bool IsStandardLayout => !IsMinimalLayout;

    public bool HasTrack => Track is not null;

    /// <summary>The track title; for a live station, the song it is playing (or the station name until that is known).</summary>
    public string Title => Track is { } track ? (track.IsLiveRadio ? LiveRadio.DisplayTitle(track, _nowPlaying) : track.Title) : string.Empty;

    /// <summary>The artists; for a live station "Artist · Station", or the station's description.</summary>
    public string Subtitle => Track is { } track ? (track.IsLiveRadio ? LiveRadio.DisplaySubtitle(track, _nowPlaying) : track.ArtistsText) : string.Empty;

    public string? CoverUrl => Track?.ThumbnailFor(120)?.Url;

    /// <summary>A live radio station is playing: no seek bar, times, likes, lyrics or related.</summary>
    public bool IsLive => Track?.IsLiveRadio == true;

    public bool IsNotLive => !IsLive;

    /// <summary>The song on the live station, when it sends one.</summary>
    public RadioNowPlaying? LiveNowPlaying => LiveRadio.IsFor(Track, _nowPlaying) ? _nowPlaying : null;

    /// <summary>Live station artwork: the song's cover when the station sends one, else its logo.</summary>
    public string? LiveArtUrl => IsLive ? LiveNowPlaying?.ArtworkUrl ?? Track?.Station?.LogoUrl : null;

    public string? StationName => Track?.Station?.Name;

    /// <summary>
    /// The live station announced its song: the title opens "On YouTube Music" (find, play, queue, like or save it)
    /// instead of toggling Now Playing.
    /// </summary>
    public bool CanFindOnYouTubeMusic => LiveNowPlaying is not null;

    public string TitleToolTip => CanFindOnYouTubeMusic ? "Find on YouTube Music" : "Now Playing";

    public bool CanSeek => HasTrack && DurationSeconds > 0;

    public bool CanRate => HasTrack && !IsLive && _auth.Status == AuthStatus.SignedIn;

    public bool IsPlaying => Status == PlaybackStatus.Playing;

    public bool IsLoading => Status is PlaybackStatus.Loading or PlaybackStatus.Buffering;

    public string PlayPauseGlyph => IsPlaying ? "" : "";

    public string PlayPauseLabel => IsPlaying ? "Pause" : "Play";

    public string VolumeGlyph => IsMuted || Volume <= 0 ? "" : Volume < 34 ? "" : Volume < 67 ? "" : "";

    public string MuteLabel => IsMuted ? "Unmute" : "Mute";

    public string ShuffleLabel => IsShuffleEnabled ? "Shuffle: on" : "Shuffle: off";

    public bool IsRepeatActive => Repeat != RepeatMode.Off;

    public string RepeatGlyph => Repeat == RepeatMode.One ? "" : "";

    public string RepeatLabel => Repeat switch
    {
        RepeatMode.All => "Repeat: all",
        RepeatMode.One => "Repeat: one",
        _ => "Repeat: off",
    };

    public string LikeGlyph => IsLiked ? "" : "";

    public string LikeLabel => IsLiked ? "Remove from liked songs" : "Like";

    /// <summary>Restores volume and the player's current state. Call once from the UI thread at startup.</summary>
    public void Initialize()
    {
        _syncing = true;
        try
        {
            Volume = Math.Clamp(_settings.Current.Volume, 0, 1) * 100;
            _player.Volume = Volume / 100;
            IsMuted = _player.IsMuted;
            IsShuffleEnabled = _queue.IsShuffled;
            Repeat = _queue.RepeatMode;
            Status = _player.Status;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the initial player state");
        }
        finally
        {
            _syncing = false;
        }

        _nowPlaying = _radio.Current;
        SetTrack(_player.CurrentTrack);

        // A restored session may already sit at a saved position (paused, not loaded yet).
        try
        {
            UpdatePosition(_player.Position, _player.Duration);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the initial position");
        }
    }

    /// <summary>The user pressed the seek slider (view glue). Position updates pause until <see cref="EndSeek"/>.</summary>
    public void BeginSeek()
    {
        _isSeeking = true;
        _seekStartValue = PositionSeconds;
    }

    /// <summary>The user released the seek slider: seek once to the final position.</summary>
    public void EndSeek()
    {
        if (!_isSeeking)
        {
            return;
        }

        _isSeeking = false;

        // A click on the track already seeked when the value jumped; a click on the thumb moves nothing.
        if (Math.Abs(PositionSeconds - _seekStartValue) >= 0.5)
        {
            SeekTo(PositionSeconds);
        }
    }

    [RelayCommand]
    private async Task PlayPauseAsync()
    {
        try
        {
            await _player.TogglePlayPauseAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Playback failed", ex);
        }
    }

    [RelayCommand]
    private async Task NextAsync()
    {
        try
        {
            await _player.NextAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not skip to the next song", ex);
        }
    }

    [RelayCommand]
    private async Task PreviousAsync()
    {
        try
        {
            await _player.PreviousAsync();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not go to the previous song", ex);
        }
    }

    [RelayCommand]
    private void ToggleShuffle()
    {
        var enabled = !IsShuffleEnabled;
        try
        {
            _player.IsShuffleEnabled = enabled;
            IsShuffleEnabled = enabled;
        }
        catch (Exception ex)
        {
            OnPropertyChanged(nameof(IsShuffleEnabled));
            _notifications.ShowError("Could not change shuffle", ex);
        }
    }

    [RelayCommand]
    private void CycleRepeat() => SetRepeat(Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    });

    /// <summary>Sets the repeat mode directly (the minimal player bar's Repeat menu).</summary>
    public void SetRepeat(RepeatMode mode)
    {
        try
        {
            _player.RepeatMode = mode;
            Repeat = mode;
        }
        catch (Exception ex)
        {
            OnPropertyChanged(nameof(IsRepeatActive));
            _notifications.ShowError("Could not change repeat", ex);
        }
    }

    [RelayCommand]
    private void ToggleMute() => IsMuted = !IsMuted;

    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        if (Track is not { } track || !CanRate)
        {
            OnPropertyChanged(nameof(IsLiked));
            return;
        }

        var wasLiked = IsLiked;
        var status = wasLiked ? LikeStatus.Indifferent : LikeStatus.Like;
        IsLiked = !wasLiked;
        try
        {
            await _accountActions.RateTrackAsync(track.VideoId, status);
            _ratings[track.VideoId] = status;
        }
        catch (Exception ex)
        {
            if (Track?.VideoId == track.VideoId)
            {
                IsLiked = wasLiked;
            }

            _notifications.ShowError(wasLiked ? "Could not remove the like" : "Could not like the song", ex);
        }
    }

    partial void OnPositionSecondsChanged(double value)
    {
        PositionText = Format.Time(TimeSpan.FromSeconds(Math.Max(0, value)));
        UpdateRemaining();
        if (_syncing || _isSeeking)
        {
            return;
        }

        // A change we didn't make ourselves: keyboard arrows or a click on the slider track.
        SeekTo(value);
    }

    partial void OnDurationSecondsChanged(double value)
    {
        DurationText = Format.Time(TimeSpan.FromSeconds(Math.Max(0, value)));
        UpdateRemaining();
    }

    private void UpdateRemaining() => RemainingText = DurationSeconds > 0
        ? "−" + Format.Time(TimeSpan.FromSeconds(Math.Max(0, Math.Round(DurationSeconds) - Math.Floor(Math.Max(0, PositionSeconds)))))
        : "0:00";

    partial void OnVolumeChanged(double value)
    {
        if (_syncing)
        {
            return;
        }

        try
        {
            _player.Volume = Math.Clamp(value / 100, 0, 1);
            if (IsMuted && value > 0)
            {
                IsMuted = false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set the volume");
        }

        _ = SaveVolumeLaterAsync(value / 100);
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (_syncing)
        {
            return;
        }

        try
        {
            _player.IsMuted = value;
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not mute", ex);
        }
    }

    private void SetTrack(Track? track)
    {
        _syncing = true;
        try
        {
            if (!LiveRadio.IsFor(track, _nowPlaying))
            {
                _nowPlaying = null;
            }

            Track = track;
            IsLiked = track is not null && (_ratings.TryGetValue(track.VideoId, out var rating) ? rating : track.LikeStatus) == LikeStatus.Like;
            _isSeeking = false;
            _lastSeekTarget = -1;
            _ignorePositionUntil = default;
            DurationSeconds = track?.Duration?.TotalSeconds ?? 0;
            PositionSeconds = 0;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void UpdatePosition(TimeSpan position, TimeSpan duration)
    {
        if (_isSeeking)
        {
            return;
        }

        var seconds = position.TotalSeconds;

        // Right after a seek the player may still report the old position; don't let the thumb jump back.
        if (DateTime.UtcNow < _ignorePositionUntil && Math.Abs(seconds - _lastSeekTarget) > 2)
        {
            return;
        }

        _syncing = true;
        try
        {
            if (duration > TimeSpan.Zero)
            {
                DurationSeconds = duration.TotalSeconds;
            }

            PositionSeconds = seconds;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SeekTo(double seconds)
    {
        if (!CanSeek || Math.Abs(seconds - _lastSeekTarget) < 0.25)
        {
            return;
        }

        _lastSeekTarget = seconds;
        _ignorePositionUntil = DateTime.UtcNow + SeekSettleTime;
        try
        {
            _player.Seek(TimeSpan.FromSeconds(seconds));
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not seek", ex);
        }
    }

    private void OnQueueChanged(QueueChangeKind kind)
    {
        if (kind is QueueChangeKind.Shuffled or QueueChangeKind.Reset or QueueChangeKind.Cleared)
        {
            try
            {
                IsShuffleEnabled = _queue.IsShuffled;
                Repeat = _queue.RepeatMode;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not read queue modes");
            }
        }
    }

    private void OnRadioNowPlaying(string stationId, RadioNowPlaying? nowPlaying)
    {
        if (Track?.Station?.Id != stationId)
        {
            return;
        }

        _nowPlaying = nowPlaying;
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(LiveNowPlaying));
        OnPropertyChanged(nameof(LiveArtUrl));
        OnPropertyChanged(nameof(CanFindOnYouTubeMusic));
        OnPropertyChanged(nameof(TitleToolTip));
    }

    private void OnTrackRated(string videoId, LikeStatus status)
    {
        _ratings[videoId] = status;
        if (Track?.VideoId == videoId)
        {
            IsLiked = status == LikeStatus.Like;
        }
    }

    private void OnPlaybackFailed(PlaybackErrorEventArgs e)
    {
        var title = e.Track is { } track ? $"Couldn't play \"{track.Title}\"" : "Playback failed";
        _notifications.Show(new AppNotification(NotificationSeverity.Error, title, e.Message, e.Exception));
    }

    private async Task SaveVolumeLaterAsync(double volume)
    {
        _volumeSaveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _volumeSaveCts = cts;
        try
        {
            // Debounced: the slider fires many changes per drag.
            await Task.Delay(VolumeSaveDelay, cts.Token);
            await _settings.UpdateAsync(s => s.Volume = Math.Clamp(volume, 0, 1), cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the volume");
        }
    }
}
