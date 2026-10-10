using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

// The music-data contracts. Implemented only by HushMusic.InnerTube.
// Everything that knows about YouTube Music's private API lives behind these interfaces.
// Methods taking "continuation" return the first page when it is null.

public interface IBrowseApi
{
    Task<Paged<Shelf>> GetHomeAsync(string? continuation = null, CancellationToken cancellationToken = default);

    Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken cancellationToken = default);

    Task<ArtistPage> GetArtistAsync(string channelId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every album or single behind an artist section's "See all": <see cref="Shelf.MoreBrowseId"/> ("MPAD...") and
    /// <see cref="Shelf.MoreParams"/>. With a continuation the browse id and params are not used.
    /// </summary>
    Task<Paged<Album>> GetArtistAlbumsAsync(string browseId, string? browseParams, string? continuation = null, CancellationToken cancellationToken = default);

    /// <summary>Accepts a playlist id with or without the "VL" prefix.</summary>
    Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default);

    Task<Paged<Track>> GetPlaylistTracksAsync(string continuation, CancellationToken cancellationToken = default);
}

public interface ISearchApi
{
    Task<SearchResults> SearchAsync(string query, SearchFilter filter = SearchFilter.All, string? continuation = null, CancellationToken cancellationToken = default);

    Task<SearchSuggestions> GetSuggestionsAsync(string input, CancellationToken cancellationToken = default);
}

/// <summary>The Explore tab: new releases, charts, moods and genres. Everything works signed out.</summary>
public interface IExploreApi
{
    Task<ExplorePage> GetExploreAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MoodCategoryGroup>> GetMoodCategoriesAsync(CancellationToken cancellationToken = default);

    /// <param name="categoryParams"><see cref="MoodCategory.Params"/>, or the <see cref="Shelf.MoreParams"/> of a mood page's shelf.</param>
    Task<MoodPage> GetMoodPlaylistsAsync(string categoryParams, CancellationToken cancellationToken = default);

    /// <param name="country">
    /// ISO 3166-1 alpha-2 code, "ZZ" for the global charts (one of <see cref="ChartsPage.Countries"/>). Null or empty
    /// lets YouTube Music pick.
    /// </param>
    Task<ChartsPage> GetChartsAsync(string? country = "ZZ", CancellationToken cancellationToken = default);

    /// <summary>Every album and single behind "New albums &amp; singles".</summary>
    Task<IReadOnlyList<Album>> GetNewReleasesAsync(CancellationToken cancellationToken = default);

    /// <summary>Every video behind "New music videos".</summary>
    Task<IReadOnlyList<Track>> GetNewVideosAsync(CancellationToken cancellationToken = default);
}

/// <summary>Signed-in user's library. Every method throws <see cref="AuthRequiredException"/> when signed out.</summary>
public interface ILibraryApi
{
    Task<Paged<Playlist>> GetPlaylistsAsync(string? continuation = null, CancellationToken cancellationToken = default);

    Task<Paged<Track>> GetSongsAsync(string? continuation = null, CancellationToken cancellationToken = default);

    Task<Paged<Album>> GetAlbumsAsync(string? continuation = null, CancellationToken cancellationToken = default);

    Task<Paged<Artist>> GetArtistsAsync(string? continuation = null, CancellationToken cancellationToken = default);

    Task<Paged<Track>> GetLikedSongsAsync(string? continuation = null, CancellationToken cancellationToken = default);

    /// <summary>Play history grouped by day ("Today", "Yesterday", ...).</summary>
    Task<IReadOnlyList<Shelf>> GetHistoryAsync(CancellationToken cancellationToken = default);
}

public interface IWatchApi
{
    /// <summary>
    /// Up-next queue for a track and/or playlist (InnerTube "next"). With <paramref name="radio"/> the
    /// result is an endless radio; keep extending it with <see cref="GetWatchPlaylistContinuationAsync"/>.
    /// </summary>
    Task<WatchPlaylist> GetWatchPlaylistAsync(string? videoId, string? playlistId = null, bool radio = false, bool shuffle = false, CancellationToken cancellationToken = default);

    Task<Paged<Track>> GetWatchPlaylistContinuationAsync(string continuation, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns null when the track has no lyrics. With <paramref name="timestamps"/> the request is sent as the mobile
    /// client (ytmusicapi <c>get_lyrics(timestamps=True)</c>) and <see cref="Lyrics.TimedLines"/> is filled when
    /// YouTube has synced lyrics; otherwise the plain text comes back.
    /// </summary>
    Task<Lyrics?> GetLyricsAsync(string lyricsBrowseId, bool timestamps = false, CancellationToken cancellationToken = default);

    /// <summary>"Related" tab of the player (<see cref="WatchPlaylist.RelatedBrowseId"/>, ytmusicapi <c>get_song_related</c>).</summary>
    Task<IReadOnlyList<Shelf>> GetRelatedAsync(string relatedBrowseId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Track loudness relative to YouTube's reference level in dB (<c>player</c> response
    /// <c>playerConfig.audioConfig.loudnessDb</c>); positive means louder than the reference. Null when unknown. Never throws
    /// for a missing value; network errors propagate.
    /// </summary>
    Task<double?> GetLoudnessDbAsync(string videoId, CancellationToken cancellationToken = default);
}

/// <summary>Write operations on the signed-in account. Prefer <see cref="IAccountActionsService"/> from UI and features.</summary>
public interface IAccountApi
{
    Task<AccountInfo?> GetAccountInfoAsync(CancellationToken cancellationToken = default);

    Task RateSongAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default);

    /// <summary>Returns the new playlist id.</summary>
    Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the new entries with their <see cref="Track.SetVideoId"/>, in the order YouTube Music reports them (empty when
    /// the response doesn't say). Without <paramref name="allowDuplicates"/> YouTube Music refuses the whole request when any
    /// of the songs is already in the playlist: that throws <see cref="AlreadyInPlaylistException"/> and nothing is added.
    /// </summary>
    Task<IReadOnlyList<PlaylistEntryRef>> AddPlaylistItemsAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default);

    /// <summary>Tracks must carry <see cref="Track.SetVideoId"/> (as returned by playlist pages).</summary>
    Task RemovePlaylistItemsAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the playlist entry <paramref name="setVideoId"/> so that it sits right before the entry
    /// <paramref name="successorSetVideoId"/>; without a successor it goes to the end.
    /// </summary>
    Task MovePlaylistItemAsync(string playlistId, string setVideoId, string? successorSetVideoId = null, CancellationToken cancellationToken = default);

    /// <summary>Null arguments are left unchanged.</summary>
    Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default);

    Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default);

    /// <summary>Marks the track as played in the user's YouTube Music history.</summary>
    Task AddHistoryItemAsync(string videoId, CancellationToken cancellationToken = default);
}
