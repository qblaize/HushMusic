using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Queue;

/// <summary>
/// "Play this" entry points: build the queue (from the watch endpoint where YouTube Music does) and start the player.
/// Unavailable tracks are skipped. Player errors surface through <see cref="IPlayer.PlaybackFailed"/>;
/// errors fetching what to play are thrown to the caller.
/// </summary>
public sealed class PlaybackActions(
    IQueueService queue,
    IPlayer player,
    IWatchApi watchApi,
    IBrowseApi browseApi,
    INotificationService notifications,
    ILogger<PlaybackActions> logger) : IPlaybackActions
{
    private readonly object _gate = new();
    private CancellationTokenSource? _fillCts;

    public async Task PlayTracksAsync(IReadOnlyList<Track> tracks, int startIndex = 0, QueueSource? source = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var (playable, start) = SelectPlayable(tracks, startIndex);
        if (playable.Count == 0)
        {
            throw new HushException("None of these tracks can be played.");
        }

        CancelPendingFill();
        queue.Load(playable, start, source ?? new QueueSource(QueueSourceKind.Manual));
        await player.PlayQueueIndexAsync(start, cancellationToken).ConfigureAwait(false);
    }

    public Task PlayTrackWithUpNextAsync(Track track, CancellationToken cancellationToken = default) =>
        PlaySeededAsync(track, radio: false, cancellationToken);

    public Task StartRadioAsync(Track track, CancellationToken cancellationToken = default) =>
        PlaySeededAsync(track, radio: true, cancellationToken);

    public Task PlayPlaylistAsync(string playlistId, bool shuffle = false, CancellationToken cancellationToken = default) =>
        PlayWatchPlaylistAsync(playlistId, shuffle, new QueueSource(QueueSourceKind.Playlist, playlistId), cancellationToken);

    public async Task PlayAlbumAsync(Album album, bool shuffle = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(album);
        var source = new QueueSource(QueueSourceKind.Album, album.BrowseId, album.Title);
        if (!string.IsNullOrEmpty(album.AudioPlaylistId))
        {
            await PlayWatchPlaylistAsync(album.AudioPlaylistId, shuffle, source, cancellationToken).ConfigureAwait(false);
            return;
        }

        var page = await browseApi.GetAlbumAsync(album.BrowseId, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(page.Album.AudioPlaylistId))
        {
            await PlayWatchPlaylistAsync(page.Album.AudioPlaylistId, shuffle, source, cancellationToken).ConfigureAwait(false);
            return;
        }

        var tracks = page.Tracks.Where(t => t.IsAvailable).Select(t => WithAlbumDetails(t, page.Album)).ToList();
        if (tracks.Count == 0)
        {
            throw new HushException("This album has no playable tracks.");
        }

        CancelPendingFill();
        queue.Load(tracks, shuffle ? Random.Shared.Next(tracks.Count) : 0, source);
        if (shuffle)
        {
            queue.SetShuffle(true);
        }

        await player.PlayQueueIndexAsync(queue.CurrentIndex, cancellationToken).ConfigureAwait(false);
    }

    public void AddToQueue(IReadOnlyList<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        queue.Enqueue([.. tracks.Where(t => t.IsAvailable)]);
    }

    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        queue.EnqueueNext([.. tracks.Where(t => t.IsAvailable)]);
    }

    private async Task PlaySeededAsync(Track track, bool radio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (track.Station is { } station)
        {
            // A live station has no YouTube "up next" or radio: it plays on its own.
            CancelPendingFill();
            queue.Load([track], 0, new QueueSource(QueueSourceKind.LiveRadio, station.Id, station.Name));
            await player.PlayQueueIndexAsync(0, cancellationToken).ConfigureAwait(false);
            return;
        }

        var fillToken = BeginFill();
        var source = new QueueSource(radio ? QueueSourceKind.Radio : QueueSourceKind.UpNext, track.VideoId, track.Title);
        queue.Load([track], 0, source);

        var play = player.PlayQueueIndexAsync(0, cancellationToken);

        // The fill is deliberately not tied to the caller's token (pages cancel theirs when the user
        // navigates away, but the queue should still fill). A newer play request cancels it instead.
        var fill = FillUpNextAsync(track, radio, source, fillToken);

        await play.ConfigureAwait(false);
        await fill.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task FillUpNextAsync(Track seed, bool radio, QueueSource source, CancellationToken cancellationToken)
    {
        try
        {
            var watch = await watchApi.GetWatchPlaylistAsync(seed.VideoId, playlistId: null, radio: radio, shuffle: false, cancellationToken)
                .ConfigureAwait(false);

            // The watch queue starts with the seed itself, which is already playing.
            var upNext = watch.Tracks.Count > 0 && watch.Tracks[0].VideoId == seed.VideoId ? watch.Tracks.Skip(1) : watch.Tracks;
            var tracks = upNext.Where(t => t.IsAvailable).ToList();

            cancellationToken.ThrowIfCancellationRequested();
            if (!ReferenceEquals(queue.Source, source))
            {
                logger.LogDebug("Queue was replaced before up next for {VideoId} arrived; dropping it", seed.VideoId);
                return;
            }

            queue.AppendContinuation(tracks, watch.Continuation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load {Kind} for {VideoId}", radio ? "radio" : "up next", seed.VideoId);
            notifications.Show(new AppNotification(
                NotificationSeverity.Warning,
                radio ? "Couldn't start radio" : "Couldn't load Up next",
                "The selected song still plays, but the rest of the queue could not be loaded.",
                ex));
        }
    }

    private async Task PlayWatchPlaylistAsync(string playlistId, bool shuffle, QueueSource source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        CancelPendingFill();
        var watch = await watchApi.GetWatchPlaylistAsync(videoId: null, playlistId, radio: false, shuffle, cancellationToken).ConfigureAwait(false);
        var tracks = watch.Tracks.Where(t => t.IsAvailable).ToList();
        if (tracks.Count == 0)
        {
            throw new HushException("This playlist is empty or unavailable.");
        }

        queue.Load(tracks, 0, source, watch.Continuation);
        await player.PlayQueueIndexAsync(0, cancellationToken).ConfigureAwait(false);
    }

    private CancellationToken BeginFill()
    {
        lock (_gate)
        {
            _fillCts?.Cancel();
            _fillCts = new CancellationTokenSource();
            return _fillCts.Token;
        }
    }

    private void CancelPendingFill()
    {
        lock (_gate)
        {
            _fillCts?.Cancel();
            _fillCts = null;
        }
    }

    /// <summary>Drops unavailable tracks and maps <paramref name="startIndex"/> to the first playable track at or after it.</summary>
    private static (List<Track> Tracks, int Start) SelectPlayable(IReadOnlyList<Track> tracks, int startIndex)
    {
        var playable = new List<Track>(tracks.Count);
        var start = -1;
        for (var i = 0; i < tracks.Count; i++)
        {
            if (!tracks[i].IsAvailable)
            {
                continue;
            }

            if (start < 0 && i >= startIndex)
            {
                start = playable.Count;
            }

            playable.Add(tracks[i]);
        }

        return (playable, Math.Max(start, 0));
    }

    // Album page tracks usually carry no artwork or album reference of their own.
    private static Track WithAlbumDetails(Track track, Album album) => track with
    {
        Thumbnails = track.Thumbnails.Count > 0 ? track.Thumbnails : album.Thumbnails,
        Album = track.Album ?? new AlbumRef(album.Title, album.BrowseId),
        Artists = track.Artists.Count > 0 ? track.Artists : album.Artists,
    };
}
