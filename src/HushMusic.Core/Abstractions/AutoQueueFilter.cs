using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>
/// What Hush leaves out of the songs it adds to the queue by itself: radio and Up next top-ups, "keep playing when the
/// queue ends", a radio started from a song (except that song) and the weekly mix. It combines smart skip
/// (<see cref="ISmartSkip"/>) with disliked songs and blocked artists (<see cref="IBlocklist"/>). Songs the user picks,
/// and the albums and playlists they play, are never filtered. Thread-safe and cheap (hash lookups). Implemented in Core.
/// </summary>
public interface IAutoQueueFilter
{
    /// <summary>Raised, on any thread, when what <see cref="IsLeftOut"/> answers may have changed.</summary>
    event EventHandler? Changed;

    /// <summary>True when the song must not be added automatically.</summary>
    bool IsLeftOut(Track track);
}

public static class AutoQueueFilterExtensions
{
    /// <summary>The tracks that may be added automatically, in their order (all of them without a filter).</summary>
    public static List<Track> WithoutLeftOut(this IAutoQueueFilter? filter, IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return filter is null ? [.. tracks] : [.. tracks.Where(t => !filter.IsLeftOut(t))];
    }
}

/// <summary>An artist whose songs Hush doesn't add by itself (<see cref="IBlocklist"/>).</summary>
/// <param name="BrowseId">The artist's channel id; null when it was blocked from a song that didn't name one.</param>
public sealed record BlockedArtist(string Name, string? BrowseId, DateTimeOffset BlockedAt)
{
    public ArtistRef ToArtistRef() => new(Name, BrowseId);
}

/// <summary>
/// Disliked songs and blocked artists, kept on this PC. <see cref="IAutoQueueFilter"/> leaves them out of the songs Hush
/// adds to the queue by itself; songs the user picks still play.
/// <list type="bullet">
/// <item>Disliked songs follow the ratings made in Hush (<see cref="IAccountActionsService.TrackRated"/>): a dislike adds
/// the song, a like or a removed dislike takes it out. A track parsed as disliked (rated elsewhere) counts too. They are
/// the account's, so signing out or switching accounts forgets them.</item>
/// <item>Blocking an artist is local only: nothing is sent to YouTube Music. A song is by a blocked artist when one of its
/// artists has the blocked channel id or, when either side has no id, the same name (case and accents aside).</item>
/// </list>
/// Thread-safe; reads are lock-free hash lookups. <see cref="Changed"/> is raised on the thread that made the change.
/// Implemented in Core.
/// </summary>
public interface IBlocklist
{
    /// <summary>Most recently blocked first.</summary>
    IReadOnlyList<BlockedArtist> BlockedArtists { get; }

    /// <summary>Raised when a song was disliked or no longer is, or an artist was blocked or unblocked (also once the saved list is read).</summary>
    event EventHandler? Changed;

    bool IsDisliked(string videoId);

    bool IsBlocked(ArtistRef artist);

    /// <summary>The song is disliked or by a blocked artist.</summary>
    bool IsLeftOut(Track track);

    /// <summary>Blocks the artist. Returns false when it was blocked already.</summary>
    Task<bool> BlockAsync(ArtistRef artist, CancellationToken cancellationToken = default);

    /// <summary>Unblocks every entry that stands for the artist. Returns false when none did.</summary>
    Task<bool> UnblockAsync(ArtistRef artist, CancellationToken cancellationToken = default);

    /// <summary>Completes once the saved list is read (it is read in the background at startup).</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}
