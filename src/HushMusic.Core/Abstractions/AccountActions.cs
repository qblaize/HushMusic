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
    ItemsMoved,
}

public sealed class PlaylistChangedEventArgs(string playlistId, PlaylistChangeKind kind, IReadOnlyList<string> videoIds) : EventArgs
{
    public string PlaylistId { get; } = playlistId;

    public PlaylistChangeKind Kind { get; } = kind;

    public IReadOnlyList<string> VideoIds { get; } = videoIds;
}

/// <summary>One entry of a playlist: <paramref name="SetVideoId"/> tells apart two entries of the same video.</summary>
public sealed record PlaylistEntryRef(string VideoId, string SetVideoId);

/// <summary>A playlist entry that was removed, and what followed it, so it can be put back where it was.</summary>
/// <param name="Track">The removed track, with the <see cref="Track.SetVideoId"/> it had.</param>
/// <param name="SuccessorSetVideoId">
/// The entry right after it before the removal (it may have been removed too). Null when it was the last entry, or when
/// the next entry isn't known.
/// </param>
public sealed record RemovedPlaylistEntry(Track Track, string? SuccessorSetVideoId);

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

    /// <summary>
    /// Adds the songs to the end of the playlist. Without <paramref name="allowDuplicates"/> YouTube Music refuses the whole
    /// request when any of them is already in the playlist: that throws <see cref="AlreadyInPlaylistException"/>, nothing is
    /// added and no event is raised.
    /// </summary>
    Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the whole playlist and adds only the songs that aren't in it yet. Returns those songs in the order given; empty,
    /// with nothing sent and no event, when every song is already there.
    /// </summary>
    Task<IReadOnlyList<string>> AddMissingToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default);

    Task RemoveFromPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves an entry of one of the user's playlists so that it sits right before <paramref name="successor"/>, or at the end
    /// when that is null. Both must carry <see cref="Track.SetVideoId"/>.
    /// </summary>
    Task MovePlaylistItemAsync(string playlistId, Track track, Track? successor, CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes <see cref="RemoveFromPlaylistAsync"/>: adds the entries again and moves each one back before its old successor.
    /// Returns the restored tracks in the order given, each with its new <see cref="Track.SetVideoId"/> (null when YouTube
    /// Music didn't report one; such an entry stays at the end of the playlist).
    /// </summary>
    Task<IReadOnlyList<Track>> RestorePlaylistItemsAsync(string playlistId, IReadOnlyList<RemovedPlaylistEntry> entries, CancellationToken cancellationToken = default);

    Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default);

    Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);
}
