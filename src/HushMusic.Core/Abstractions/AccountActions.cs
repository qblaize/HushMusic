using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

public sealed class TrackRatedEventArgs(string videoId, LikeStatus status) : EventArgs
{
    public string VideoId { get; } = videoId;

    public LikeStatus Status { get; } = status;
}

public enum PlaylistChangeKind
{
    Created,
    ItemsAdded,
    ItemsRemoved,
    Edited,
    Deleted,
}

public sealed class PlaylistChangedEventArgs(string playlistId, PlaylistChangeKind kind, IReadOnlyList<string> videoIds) : EventArgs
{
    public string PlaylistId { get; } = playlistId;

    public PlaylistChangeKind Kind { get; } = kind;

    public IReadOnlyList<string> VideoIds { get; } = videoIds;
}

/// <summary>
/// Account write actions for UI and features. Wraps <see cref="IAccountApi"/> and raises an event
/// after each successful change so other parts of the app (and custom features) can react.
/// Events are raised on the calling thread.
/// </summary>
public interface IAccountActionsService
{
    event EventHandler<TrackRatedEventArgs>? TrackRated;

    event EventHandler<PlaylistChangedEventArgs>? PlaylistChanged;

    Task RateTrackAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default);

    Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default);

    Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);

    Task RemoveFromPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);

    Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default);

    Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);
}
