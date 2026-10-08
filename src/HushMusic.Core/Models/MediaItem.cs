namespace HushMusic.Core.Models;

/// <summary>
/// Base type for anything that can appear in a shelf, search result or list:
/// <see cref="Track"/>, <see cref="Album"/>, <see cref="Artist"/>, <see cref="Playlist"/>.
/// </summary>
public abstract record MediaItem
{
    public required string Title { get; init; }

    public IReadOnlyList<Thumbnail> Thumbnails { get; init; } = [];

    /// <summary>Largest thumbnail, or null when none were parsed.</summary>
    public Thumbnail? BestThumbnail => Thumbnail.Largest(Thumbnails);

    /// <summary>Smallest thumbnail at least <paramref name="minWidth"/> wide (falls back to the largest).</summary>
    public Thumbnail? ThumbnailFor(int minWidth) => Thumbnail.AtLeast(Thumbnails, minWidth);
}

/// <summary>A song, music video or episode. Playable by <see cref="VideoId"/>.</summary>
public sealed record Track : MediaItem
{
    public required string VideoId { get; init; }

    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];

    public AlbumRef? Album { get; init; }

    public TimeSpan? Duration { get; init; }

    public TrackType Type { get; init; } = TrackType.Song;

    public bool IsExplicit { get; init; }

    public bool IsAvailable { get; init; } = true;

    public LikeStatus? LikeStatus { get; init; }

    /// <summary>Per-playlist item id. Required to remove or move an item inside a playlist.</summary>
    public string? SetVideoId { get; init; }

    /// <summary>Playlist the track was listed in (watch endpoint context), if any.</summary>
    public string? PlaylistId { get; init; }

    /// <summary>View/play count text as shown by YouTube Music, e.g. "1.2B plays".</summary>
    public string? Views { get; init; }

    /// <summary>Tokens for adding/removing the track from the library, when present.</summary>
    public FeedbackTokens? FeedbackTokens { get; init; }

    /// <summary>
    /// Set when this item is a live internet radio station rather than a YouTube track (see <see cref="LiveRadio"/>):
    /// <see cref="VideoId"/> is then "radio:…" and must never reach YouTube.
    /// </summary>
    public RadioStation? Station { get; init; }

    /// <summary>A live radio stream: no duration, no seeking, no likes, lyrics or history.</summary>
    public bool IsLiveRadio => Station is not null;

    public string ArtistsText => string.Join(", ", Artists.Select(a => a.Name));
}

public sealed record Album : MediaItem
{
    /// <summary>Album browse id, usually starting with MPREb_.</summary>
    public required string BrowseId { get; init; }

    public IReadOnlyList<ArtistRef> Artists { get; init; } = [];

    public string? Year { get; init; }

    public AlbumType Type { get; init; } = AlbumType.Album;

    public bool IsExplicit { get; init; }

    /// <summary>Playlist id (OLAK5uy_...) used to play the whole album.</summary>
    public string? AudioPlaylistId { get; init; }

    public string ArtistsText => string.Join(", ", Artists.Select(a => a.Name));
}

public sealed record Artist : MediaItem
{
    /// <summary>Channel browse id, usually starting with UC.</summary>
    public required string BrowseId { get; init; }

    public string? Subscribers { get; init; }
}

public sealed record Playlist : MediaItem
{
    /// <summary>Playlist id without the "VL" browse prefix.</summary>
    public required string PlaylistId { get; init; }

    public ArtistRef? Author { get; init; }

    public int? TrackCount { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Auto-generated mix or radio station with no browse page: play it with
    /// <c>IPlaybackActions.PlayPlaylistAsync</c> instead of opening it.
    /// </summary>
    public bool IsMix { get; init; }
}
