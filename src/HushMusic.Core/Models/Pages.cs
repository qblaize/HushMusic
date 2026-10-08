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

/// <summary>
/// The Explore tab: new releases, chart songs, moods and genres, new music videos. A section YouTube Music
/// leaves out is empty (lists) or null (charts).
/// </summary>
public sealed record ExplorePage
{
    /// <summary>"New albums &amp; singles".</summary>
    public IReadOnlyList<Album> NewReleases { get; init; } = [];

    /// <summary>"Top songs". YouTube Music only sends it to Premium accounts.</summary>
    public SongChart? TopSongs { get; init; }

    /// <summary>"Trending": the songs and videos climbing in the listener's country.</summary>
    public SongChart? Trending { get; init; }

    public IReadOnlyList<MoodCategory> MoodsAndGenres { get; init; } = [];

    /// <summary>"New music videos".</summary>
    public IReadOnlyList<Track> NewVideos { get; init; } = [];
}

/// <summary>A chart playlist shown as a ranked list of songs ("Top songs", "Trending").</summary>
public sealed record SongChart
{
    public required string Title { get; init; }

    /// <summary>The whole chart as a playlist (id without "VL"), for "See all".</summary>
    public string? PlaylistId { get; init; }

    public IReadOnlyList<ChartEntry<Track>> Entries { get; init; } = [];
}

/// <summary>One chart position. <see cref="Rank"/> and <see cref="Trend"/> are null when YouTube Music doesn't send them.</summary>
public sealed record ChartEntry<T>(T Item, int? Rank, ChartTrend? Trend)
    where T : MediaItem;

public enum ChartTrend
{
    Neutral,
    Up,
    Down,
}

/// <summary>A "Moods &amp; genres" category. Its page comes from <c>IExploreApi.GetMoodPlaylistsAsync(Params)</c>.</summary>
public sealed record MoodCategory(string Title, string Params)
{
    /// <summary>The colour YouTube Music marks the category with (0xAARRGGBB), when it sends one.</summary>
    public uint? Color { get; init; }
}

/// <summary>A titled group of categories ("Moods &amp; moments", "Genres").</summary>
public sealed record MoodCategoryGroup(string Title, IReadOnlyList<MoodCategory> Categories);

/// <summary>
/// A mood or genre page: shelves of playlists, and for genres also songs, videos and albums. A shelf's
/// <see cref="Shelf.MoreParams"/> opens a narrower category with <c>IExploreApi.GetMoodPlaylistsAsync</c>.
/// </summary>
public sealed record MoodPage
{
    public required string Title { get; init; }

    public IReadOnlyList<Shelf> Sections { get; init; } = [];
}

/// <summary>The charts of one country.</summary>
public sealed record ChartsPage
{
    /// <summary>The country shown, as YouTube Music names it ("United States", "Global").</summary>
    public string? CountryName { get; init; }

    /// <summary>Countries that have charts: ISO 3166-1 alpha-2 codes, "ZZ" for Global.</summary>
    public IReadOnlyList<string> Countries { get; init; } = [];

    /// <summary>Carousels of chart playlists ("Video charts", and "Genres" in some countries); the items are <see cref="Playlist"/>s.</summary>
    public IReadOnlyList<Shelf> PlaylistCharts { get; init; } = [];

    public IReadOnlyList<ChartEntry<Artist>> TopArtists { get; init; } = [];
}
