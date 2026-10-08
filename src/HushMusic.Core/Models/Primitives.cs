namespace HushMusic.Core.Models;

public sealed record Thumbnail(string Url, int Width, int Height)
{
    public static Thumbnail? Largest(IReadOnlyList<Thumbnail> thumbnails) =>
        thumbnails.Count == 0 ? null : thumbnails.MaxBy(t => t.Width * t.Height);

    public static Thumbnail? AtLeast(IReadOnlyList<Thumbnail> thumbnails, int minWidth) =>
        thumbnails.Where(t => t.Width >= minWidth).MinBy(t => t.Width) ?? Largest(thumbnails);
}

/// <summary>Artist reference inside a track/album. <see cref="BrowseId"/> is null for plain-text credits.</summary>
public sealed record ArtistRef(string Name, string? BrowseId);

public sealed record AlbumRef(string Name, string? BrowseId);

public sealed record FeedbackTokens(string? Add, string? Remove);

public sealed record AccountInfo(string Name, string? ChannelHandle, string? PhotoUrl);

/// <summary>
/// One page of results. <see cref="Continuation"/> is an opaque token: pass it back to the
/// same API method to get the next page. Null means there are no more pages.
/// </summary>
public sealed record Paged<T>(IReadOnlyList<T> Items, string? Continuation)
{
    public bool HasMore => Continuation is not null;

    public static Paged<T> Empty { get; } = new([], null);
}

public enum TrackType
{
    Song,
    Video,
    Episode,
    Unknown,
}

public enum AlbumType
{
    Album,
    Single,
    EP,
    Unknown,
}

public enum LikeStatus
{
    Indifferent,
    Like,
    Dislike,
}

public enum PrivacyStatus
{
    Public,
    Unlisted,
    Private,
}

public enum SearchFilter
{
    All,
    Songs,
    Videos,
    Albums,
    Artists,
    CommunityPlaylists,
    FeaturedPlaylists,
}
