using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Windows.Media;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Playback;

/// <summary>
/// Drives the System Media Transport Controls (media keys, Windows media flyout, lock screen) by hand.
/// MediaPlayer's own command manager is turned off because the queue lives in Core, not in a MediaPlaybackList.
/// </summary>
internal sealed class SmtcController : IDisposable
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly IPlayer _player;
    private readonly ILogger _logger;

    public SmtcController(MediaPlayer mediaPlayer, IPlayer player, ILogger logger)
    {
        _player = player;
        _logger = logger;

        mediaPlayer.CommandManager.IsEnabled = false;
        _smtc = mediaPlayer.SystemMediaTransportControls;
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        _smtc.ButtonPressed += OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested += OnPlaybackPositionChangeRequested;
    }

    /// <summary>
    /// Shows <paramref name="track"/>. A live station shows its current song (<paramref name="nowPlaying"/>) as the title,
    /// "Artist · Station" as the artist, and no timeline.
    /// </summary>
    public void SetTrack(Track? track, RadioNowPlaying? nowPlaying = null) => Try(() =>
    {
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        if (track is not null)
        {
            updater.Type = MediaPlaybackType.Music;
            if (track.IsLiveRadio)
            {
                updater.MusicProperties.Title = LiveRadio.DisplayTitle(track, nowPlaying);
                updater.MusicProperties.Artist = LiveRadio.DisplaySubtitle(track, nowPlaying);
                updater.MusicProperties.AlbumTitle = track.Title;
            }
            else
            {
                updater.MusicProperties.Title = track.Title;
                updater.MusicProperties.Artist = track.ArtistsText;
                updater.MusicProperties.AlbumTitle = track.Album?.Name ?? string.Empty;
            }

            var art = LiveRadio.IsFor(track, nowPlaying) && nowPlaying!.ArtworkUrl is { } cover ? ToUri(cover) : ThumbnailUri(track);
            if (art is not null)
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromUri(art);
            }
        }

        updater.Update();
        if (track?.IsLiveRadio == true)
        {
            // An empty timeline hides the seek bar and times in the media flyout.
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties());
        }
    });

    public void SetStatus(PlaybackStatus status, bool hasTrack) => Try(() =>
        _smtc.PlaybackStatus = status switch
        {
            PlaybackStatus.Playing => MediaPlaybackStatus.Playing,
            PlaybackStatus.Paused => MediaPlaybackStatus.Paused,
            PlaybackStatus.Loading or PlaybackStatus.Buffering => MediaPlaybackStatus.Changing,
            _ when hasTrack => MediaPlaybackStatus.Stopped,
            _ => MediaPlaybackStatus.Closed,
        });

    public void SetTimeline(TimeSpan position, TimeSpan duration) => Try(() =>
    {
        if (duration <= TimeSpan.Zero)
        {
            return;
        }

        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            Position = position < TimeSpan.Zero ? TimeSpan.Zero : position > duration ? duration : position,
            MaxSeekTime = duration,
            EndTime = duration,
        });
    });

    public void Dispose()
    {
        _smtc.ButtonPressed -= OnButtonPressed;
        _smtc.PlaybackPositionChangeRequested -= OnPlaybackPositionChangeRequested;
        Try(() =>
        {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
        });
    }

    private static Uri? ThumbnailUri(Track track) => ToUri(track.BestThumbnail?.Url);

    private static Uri? ToUri(string? url)
    {
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = "https:" + url;
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
    }

    // Raised on a system thread.
    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        _ = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => RunAsync(() => _player.PlayAsync()),
            SystemMediaTransportControlsButton.Pause => RunAsync(() => { _player.Pause(); return Task.CompletedTask; }),
            SystemMediaTransportControlsButton.Stop => RunAsync(() => { _player.Stop(); return Task.CompletedTask; }),
            SystemMediaTransportControlsButton.Next => RunAsync(() => _player.NextAsync()),
            SystemMediaTransportControlsButton.Previous => RunAsync(() => _player.PreviousAsync()),
            _ => Task.CompletedTask,
        };
    }

    private void OnPlaybackPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        _ = RunAsync(() =>
        {
            _player.Seek(args.RequestedPlaybackPosition);
            return Task.CompletedTask;
        });

    private async Task RunAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Media key command failed");
        }
    }

    private void Try(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ObjectDisposedException or ArgumentException)
        {
            _logger.LogWarning(ex, "Could not update the system media controls");
        }
    }
}
