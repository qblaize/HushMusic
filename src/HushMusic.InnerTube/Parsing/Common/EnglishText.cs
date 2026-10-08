using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Every place where parsing depends on English display text (requests are sent with hl=en).
/// ytmusicapi matches these through its gettext catalogs; if YouTube rewords or the locale
/// changes, this is the only file to fix. Structural signals (pageType, musicVideoType,
/// browseId prefixes, icon types) are used everywhere else.
/// </summary>
internal static class EnglishText
{
    /// <summary>Album/release type word that starts album subtitles ("Single • 2023").</summary>
    public static AlbumType? ParseAlbumType(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "album" => AlbumType.Album,
        "single" => AlbumType.Single,
        "ep" => AlbumType.EP,
        _ => AlbumType.Unknown,
    };

    /// <summary>Playlist rows titled like this are deleted songs and dropped (ytmusicapi parse_playlist_item).</summary>
    public const string SongDeleted = "Song deleted";

    /// <summary>Fallback category of the search top-result card when it has no header (ytmusicapi parse_top_result).</summary>
    public const string TopResult = "Top result";

    /// <summary>
    /// Result type of the search top-result card from its first subtitle run ("Artist • 79.6M monthly audience").
    /// Used only when the card's endpoints do not identify it. Any other word ("Single", "EP") means album.
    /// </summary>
    public static string? TopResultType(string? subtitleWord)
    {
        if (string.IsNullOrWhiteSpace(subtitleWord))
        {
            return null;
        }

        var word = subtitleWord.Trim().ToLowerInvariant();
        return word is SearchTypes.Album or SearchTypes.Artist or SearchTypes.Playlist or SearchTypes.Song
            or SearchTypes.Video or SearchTypes.Station or SearchTypes.Profile or SearchTypes.Podcast or SearchTypes.Episode
            ? word
            : SearchTypes.Album;
    }

    /// <summary>
    /// Filtered search keeps a shelf only when its title contains the filter's singular word
    /// ("song" in "Songs", "playlist" in "Community playlists"); YouTube sometimes pads results with
    /// shelves of another type.
    /// </summary>
    public static bool ShelfMatchesType(string shelfTitle, string resultType) =>
        shelfTitle.Contains(resultType, StringComparison.OrdinalIgnoreCase);

    /// <summary>"35 songs" / "1 song" -> count. View counts ("3K views") return null.</summary>
    public static int? ParseSongCount(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var parts = text.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[1] is "songs" or "song" ? TextRuns.ParseInt(parts[0]) : null;
    }
}

/// <summary>ytmusicapi's search result type names.</summary>
internal static class SearchTypes
{
    public const string Album = "album";
    public const string Artist = "artist";
    public const string Playlist = "playlist";
    public const string Song = "song";
    public const string Video = "video";
    public const string Station = "station";
    public const string Profile = "profile";
    public const string Podcast = "podcast";
    public const string Episode = "episode";
}
