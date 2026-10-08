using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Services;

public sealed class AccountActionsService(IAccountApi api) : IAccountActionsService
{
    public event EventHandler<TrackRatedEventArgs>? TrackRated;

    public event EventHandler<PlaylistChangedEventArgs>? PlaylistChanged;

    public async Task RateTrackAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default)
    {
        await api.RateSongAsync(videoId, status, cancellationToken).ConfigureAwait(false);
        TrackRated?.Invoke(this, new TrackRatedEventArgs(videoId, status));
    }

    public async Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default)
    {
        var playlistId = await api.CreatePlaylistAsync(title, description, privacy, videoIds, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Created, videoIds ?? []));
        return playlistId;
    }

    public async Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default)
    {
        await api.AddPlaylistItemsAsync(playlistId, videoIds, allowDuplicates: false, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.ItemsAdded, videoIds));
    }

    public async Task RemoveFromPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        await api.RemovePlaylistItemsAsync(playlistId, tracks, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.ItemsRemoved, [.. tracks.Select(t => t.VideoId)]));
    }

    public async Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default)
    {
        await api.EditPlaylistAsync(playlistId, title, description, privacy, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Edited, []));
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        await api.DeletePlaylistAsync(playlistId, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Deleted, []));
    }
}
