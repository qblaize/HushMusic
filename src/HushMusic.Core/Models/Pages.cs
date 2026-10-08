namespace HushMusic.Core.Models;

/// <summary>
/// A titled row of items: a home-feed shelf, an artist page section, a search result section
/// or a history day group.
/// </summary>
public sealed record Shelf
{
    public required string Title { get; init; }

    public string? Subtitle { get; init; }

    public IReadOnlyList<MediaItem> Items { get; init; } = [];

    /// <summary>Browse id behind the shelf's "More" button, when it has one.</summary>
    public string? MoreBrowseId { get; init; }

    /// <summary>Browse params that go with <see cref="MoreBrowseId"/>.</summary>
    public string? MoreParams { get; init; }

    /// <summary>How YouTube Music lays the shelf out (cards, or a grid of compact song rows like "Quick picks").</summary>
    public ShelfLayout Layout { get; init; }
}

public enum ShelfLayout
{
    /// <summary>Carousel of square/wide cards (musicTwoRowItemRenderer).</summary>
    Cards,

    /// <summary>Grid of compact song rows (musicResponsiveListItemRenderer in a carousel), e.g. "Quick picks".</summary>
    List,
}

public sealed record AlbumPage
{
    public required Album Album { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<Track> Tracks { get; init; } = [];

    public int? TrackCount { get; init; }

    /// <summary>Total duration as displayed, e.g. "48 minutes".</summary>
    public string? DurationText { get; init; }

    public IReadOnlyList<Album> OtherVersions { get; init; } = [];
}

public sealed record ArtistPage
{
    public required Artist Artist { get; init; }

    public string? Description { get; init; }

    public string? Views { get; init; }

    /// <summary>Playlist id that plays the artist's songs shuffled.</summary>
    public string? ShufflePlaylistId { get; init; }

    /// <summary>Playlist id for the artist radio.</summary>
    public string? RadioPlaylistId { get; init; }

    public IReadOnlyList<Track> TopSongs { get; init; } = [];

    /// <summary>Playlist id behind "Songs → See all", when present.</summary>
    public string? AllSongsPlaylistId { get; init; }

    /// <summary>Remaining sections (albums, singles, videos, playlists, related artists...) in page order.</summary>
    public IReadOnlyList<Shelf> Sections { get; init; } = [];
}

public sealed record PlaylistPage
{
    public required Playlist Playlist { get; init; }

    public PrivacyStatus? Privacy { get; init; }

    /// <summary>True when the signed-in user owns the playlist and can edit it.</summary>
    public bool IsOwned { get; init; }

    public string? Year { get; init; }

    public string? DurationText { get; init; }

    /// <summary>First page of tracks; continue with <c>IBrowseApi.GetPlaylistTracksAsync</c>.</summary>
    public Paged<Track> Tracks { get; init; } = Paged<Track>.Empty;
}

public sealed record SearchResults
{
    public MediaItem? TopResult { get; init; }

    /// <summary>Unfiltered search returns several sections; a filtered search returns one.</summary>
    public IReadOnlyList<Shelf> Sections { get; init; } = [];

    public string? Continuation { get; init; }

    public IEnumerable<MediaItem> AllItems => Sections.SelectMany(s => s.Items);
}

public sealed record SearchSuggestions(IReadOnlyList<string> Queries, IReadOnlyList<MediaItem> Items)
{
    public static SearchSuggestions Empty { get; } = new([], []);
}

public sealed record WatchPlaylist
{
    public IReadOnlyList<Track> Tracks { get; init; } = [];

    public string? PlaylistId { get; init; }

    public string? LyricsBrowseId { get; init; }

    public string? RelatedBrowseId { get; init; }

    /// <summary>Opaque token for <c>IWatchApi.GetWatchPlaylistContinuationAsync</c> (radio keeps going).</summary>
    public string? Continuation { get; init; }
}

public sealed record Lyrics
{
    public required string Text { get; init; }

    public string? Source { get; init; }

    /// <summary>Synced lines when YouTube Music provides them; null otherwise.</summary>
    public IReadOnlyList<TimedLyricLine>? TimedLines { get; init; }
}

public sealed record TimedLyricLine(TimeSpan Start, TimeSpan End, string Text);
