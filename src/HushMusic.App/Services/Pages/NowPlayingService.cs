using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// The track the player is on, for "now playing" highlights in lists and cards.
/// A UI-thread mirror of <see cref="IPlayer.TrackChanged"/> / <see cref="IPlayer.StatusChanged"/>.
/// </summary>
public interface INowPlayingService
{
    /// <summary>Video id of the player's current track, or null when nothing is loaded.</summary>
    string? CurrentVideoId { get; }

    /// <summary>True while the current track is playing (or about to): drives the animated equaliser.</summary>
    bool IsPlaying { get; }

    /// <summary>Raised on the UI thread when <see cref="CurrentVideoId"/> or <see cref="IsPlaying"/> changes.</summary>
    event EventHandler? Changed;

    bool IsCurrent(string? videoId);
}

internal sealed class NowPlayingService : INowPlayingService
{
    private readonly IUiDispatcher _dispatcher;
    private volatile string? _videoId;
    private volatile PlaybackStatus _status;

    public NowPlayingService(IPlayer player, IUiDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _videoId = player.CurrentTrack?.VideoId;
        _status = player.Status;
        CurrentVideoId = _videoId;
        IsPlaying = IsActive(_status);

        player.TrackChanged += (_, e) =>
        {
            _videoId = e.Track?.VideoId;
            Publish();
        };
        player.StatusChanged += (_, e) =>
        {
            _status = e.Status;
            Publish();
        };
    }

    public event EventHandler? Changed;

    public string? CurrentVideoId { get; private set; }

    public bool IsPlaying { get; private set; }

    public bool IsCurrent(string? videoId) =>
        !string.IsNullOrEmpty(videoId) && string.Equals(videoId, CurrentVideoId, StringComparison.Ordinal);

    private static bool IsActive(PlaybackStatus status) =>
        status is PlaybackStatus.Playing or PlaybackStatus.Loading or PlaybackStatus.Buffering;

    // Player events arrive on background threads; the latest values are read when the UI thread runs.
    private void Publish() => _dispatcher.Run(() =>
    {
        var videoId = _videoId;
        var playing = videoId is not null && IsActive(_status);
        if (videoId == CurrentVideoId && playing == IsPlaying)
        {
            return;
        }

        CurrentVideoId = videoId;
        IsPlaying = playing;
        Changed?.Invoke(this, EventArgs.Empty);
    });
}
