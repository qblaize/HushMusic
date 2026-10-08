using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// What happens when the user activates or acts on a media item, shared by every page, card, row and menu.
/// Methods never throw: failures are reported as InfoBars.
/// </summary>
public interface IMediaItemActions
{
    bool IsSignedIn { get; }

    /// <summary>
    /// Album/Artist/Playlist navigate to their page; a track (or <see cref="TrackItem"/>) plays with up next;
    /// a mix (<see cref="Playlist.IsMix"/>) starts playing because it has no page.
    /// </summary>
    void Open(object? item);

    /// <summary>The play button on a card: plays an album/playlist/track; artists open their page.</summary>
    Task PlayAsync(MediaItem? item);

    Task PlayTrackAsync(Track track);

    Task PlayTracksAsync(IReadOnlyList<Track> tracks, int startIndex, QueueSource source);

    Task PlayPlaylistAsync(string playlistId, bool shuffle = false);

    Task PlayAlbumAsync(Album album, bool shuffle = false);

    Task StartRadioAsync(Track track);

    void PlayNext(IReadOnlyList<Track> tracks);

    void AddToQueue(IReadOnlyList<Track> tracks);

    LikeStatus GetLikeStatus(Track track);

    Task RateAsync(Track track, LikeStatus status);

    /// <summary>Asks for a playlist (or a new one) and adds the tracks to it.</summary>
    Task AddToPlaylistAsync(IReadOnlyList<Track> tracks);

    void OpenAlbum(string? browseId);

    void OpenArtist(string? channelId);

    void OpenPlaylist(string? playlistId);
}

// Playback and account actions are not tied to the page that started them (the user may navigate away
// right after pressing Play), so they run without the page's cancellation token.
internal sealed class MediaItemActions(
    IPlaybackActions playback,
    IAccountActionsService account,
    IAuthService auth,
    ILikeStateService likes,
    IPlaylistDialogService playlistDialogs,
    INavigationService navigation,
    INavigationPreviews previews,
    INotificationService notifications) : IMediaItemActions
{
    public bool IsSignedIn => auth.Status == AuthStatus.SignedIn;

    public void Open(object? item)
    {
        switch (item)
        {
            case TrackItem trackItem:
                _ = PlayTrackAsync(trackItem.Track);
                break;
            case Track track:
                _ = PlayTrackAsync(track);
                break;
            case Album album:
                previews.Remember(album);
                OpenAlbum(album.BrowseId);
                break;
            case Artist artist:
                previews.Remember(artist);
                OpenArtist(artist.BrowseId);
                break;
            case Playlist { IsMix: true } mix:
                // Mixes and radio stations have no browse page; they only exist as a watch queue.
                _ = PlayPlaylistAsync(mix.PlaylistId);
                break;
            case Playlist playlist:
                previews.Remember(playlist);
                OpenPlaylist(playlist.PlaylistId);
                break;
        }
    }

    public Task PlayAsync(MediaItem? item)
    {
        switch (item)
        {
            case Track track:
                return PlayTrackAsync(track);
            case Album album:
                return PlayAlbumAsync(album);
            case Playlist playlist:
                return PlayPlaylistAsync(playlist.PlaylistId);
            case Artist artist:
                OpenArtist(artist.BrowseId);
                break;
        }

        return Task.CompletedTask;
    }

    public Task PlayTrackAsync(Track track) =>
        TryAsync(() => playback.PlayTrackWithUpNextAsync(track), "Couldn't play this song");

    public Task PlayTracksAsync(IReadOnlyList<Track> tracks, int startIndex, QueueSource source) =>
        tracks.Count == 0
            ? Task.CompletedTask
            : TryAsync(() => playback.PlayTracksAsync(tracks, Math.Clamp(startIndex, 0, tracks.Count - 1), source), "Couldn't start playback");

    public Task PlayPlaylistAsync(string playlistId, bool shuffle = false) =>
        TryAsync(() => playback.PlayPlaylistAsync(playlistId, shuffle), "Couldn't play this playlist");

    public Task PlayAlbumAsync(Album album, bool shuffle = false) =>
        TryAsync(() => playback.PlayAlbumAsync(album, shuffle), "Couldn't play this album");

    public Task StartRadioAsync(Track track) =>
        TryAsync(() => playback.StartRadioAsync(track), "Couldn't start the radio");

    public void PlayNext(IReadOnlyList<Track> tracks)
    {
        if (Try(() => playback.PlayNext(tracks), "Couldn't change the queue"))
        {
            notifications.Show(new AppNotification(NotificationSeverity.Success, "Playing next", Describe(tracks)));
        }
    }

    public void AddToQueue(IReadOnlyList<Track> tracks)
    {
        if (Try(() => playback.AddToQueue(tracks), "Couldn't change the queue"))
        {
            notifications.Show(new AppNotification(NotificationSeverity.Success, "Added to queue", Describe(tracks)));
        }
    }

    public LikeStatus GetLikeStatus(Track track) => likes.GetStatus(track);

    public Task RateAsync(Track track, LikeStatus status) =>
        TryAsync(
            async () =>
            {
                await account.RateTrackAsync(track.VideoId, status);
                var title = status == LikeStatus.Like ? "Added to your liked songs" : "Removed from your liked songs";
                notifications.Show(new AppNotification(NotificationSeverity.Success, title, track.Title));
            },
            "Couldn't update the like");

    public Task AddToPlaylistAsync(IReadOnlyList<Track> tracks)
    {
        var videoIds = tracks.Select(t => t.VideoId).Distinct(StringComparer.Ordinal).ToList();
        if (videoIds.Count == 0)
        {
            return Task.CompletedTask;
        }

        return TryAsync(
            async () =>
            {
                var choice = await playlistDialogs.PickPlaylistAsync();
                if (choice is null)
                {
                    return;
                }

                if (choice.Playlist is { } playlist)
                {
                    await account.AddToPlaylistAsync(playlist.PlaylistId, videoIds);
                    notifications.Show(new AppNotification(NotificationSeverity.Success, "Added to playlist", playlist.Title));
                    return;
                }

                var details = await playlistDialogs.PromptCreateAsync();
                if (details is null)
                {
                    return;
                }

                await account.CreatePlaylistAsync(details.Title, details.Description, details.Privacy, videoIds);
                notifications.Show(new AppNotification(NotificationSeverity.Success, "Playlist created", details.Title));
            },
            "Couldn't add to the playlist");
    }

    public void OpenAlbum(string? browseId) => NavigateIfSet(PageKey.Album, browseId);

    public void OpenArtist(string? channelId) => NavigateIfSet(PageKey.Artist, channelId);

    public void OpenPlaylist(string? playlistId) => NavigateIfSet(PageKey.Playlist, playlistId);

    private void NavigateIfSet(PageKey page, string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            navigation.NavigateTo(page, id);
        }
    }

    private static string Describe(IReadOnlyList<Track> tracks) =>
        tracks.Count == 1 ? tracks[0].Title : $"{tracks.Count} songs";

    private async Task TryAsync(Func<Task> action, string errorTitle)
    {
        try
        {
            await action();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            notifications.ShowError(errorTitle, ex);
        }
    }

    private bool Try(Action action, string errorTitle)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            notifications.ShowError(errorTitle, ex);
            return false;
        }
    }
}
