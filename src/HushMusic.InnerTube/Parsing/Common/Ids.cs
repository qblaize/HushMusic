using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary><c>browseEndpointContextMusicConfig.pageType</c> values.</summary>
internal static class PageTypes
{
    public const string Album = "MUSIC_PAGE_TYPE_ALBUM";
    public const string Audiobook = "MUSIC_PAGE_TYPE_AUDIOBOOK";
    public const string Artist = "MUSIC_PAGE_TYPE_ARTIST";
    public const string UserChannel = "MUSIC_PAGE_TYPE_USER_CHANNEL";
    public const string Playlist = "MUSIC_PAGE_TYPE_PLAYLIST";
    public const string PodcastShow = "MUSIC_PAGE_TYPE_PODCAST_SHOW_DETAIL_PAGE";
    public const string NonMusicAudioTrack = "MUSIC_PAGE_TYPE_NON_MUSIC_AUDIO_TRACK_PAGE";
    public const string Unknown = "MUSIC_PAGE_TYPE_UNKNOWN";
    public const string TrackLyrics = "MUSIC_PAGE_TYPE_TRACK_LYRICS";
    public const string TrackRelated = "MUSIC_PAGE_TYPE_TRACK_RELATED";

    /// <summary>pageType of a node that has a <c>browseEndpoint</c> child (a run's navigationEndpoint, an onTap...).</summary>
    public static string? Of(JsonNode? endpointHolder) =>
        endpointHolder.Str("browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType");

    public static bool IsAlbum(string? pageType) => pageType is Album or Audiobook;

    public static bool IsArtist(string? pageType) => pageType is Artist or UserChannel;
}

/// <summary><c>watchEndpointMusicConfig.musicVideoType</c> values.</summary>
internal static class VideoTypes
{
    public const string Atv = "MUSIC_VIDEO_TYPE_ATV";
    public const string Omv = "MUSIC_VIDEO_TYPE_OMV";
    public const string Ugc = "MUSIC_VIDEO_TYPE_UGC";
    public const string OfficialSourceMusic = "MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC";
    public const string PodcastEpisode = "MUSIC_VIDEO_TYPE_PODCAST_EPISODE";
    public const string PrivatelyOwnedTrack = "MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK";

    /// <summary>musicVideoType of a node that has a <c>watchEndpoint</c> child.</summary>
    public static string? Of(JsonNode? endpointHolder) =>
        endpointHolder.Str("watchEndpoint", "watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig", "musicVideoType");

    /// <summary>
    /// Maps musicVideoType to <see cref="TrackType"/>. Values ytmusicapi does not know
    /// (e.g. MUSIC_VIDEO_TYPE_SHOULDER, seen in playlists) become <see cref="TrackType.Unknown"/>.
    /// </summary>
    public static TrackType ToTrackType(string? videoType, TrackType whenMissing) => videoType switch
    {
        null => whenMissing,
        Atv or PrivatelyOwnedTrack => TrackType.Song,
        Omv or Ugc or OfficialSourceMusic => TrackType.Video,
        PodcastEpisode => TrackType.Episode,
        _ => TrackType.Unknown,
    };
}

internal static class BrowseIds
{
    /// <summary>Playlist browse ids are "VL" + playlistId; models carry the bare playlist id.</summary>
    public static string StripVl(string id) => id.StartsWith("VL", StringComparison.Ordinal) ? id[2..] : id;

    /// <summary>Artist pages accept the channel id; ytmusicapi strips a leading "MPLA".</summary>
    public static string StripMpla(string id) => id.StartsWith("MPLA", StringComparison.Ordinal) ? id[4..] : id;

    /// <summary>Result type from a browseId prefix (ytmusicapi parse_search_result).</summary>
    public static string? KindOf(string? browseId) => browseId switch
    {
        null => null,
        _ when browseId.StartsWith("VM", StringComparison.Ordinal)
            || browseId.StartsWith("RD", StringComparison.Ordinal)
            || browseId.StartsWith("VL", StringComparison.Ordinal) => SearchTypes.Playlist,
        _ when browseId.StartsWith("MPLA", StringComparison.Ordinal)
            || browseId.StartsWith("UC", StringComparison.Ordinal) => SearchTypes.Artist,
        _ when browseId.StartsWith("MPRE", StringComparison.Ordinal) => SearchTypes.Album,
        _ when browseId.StartsWith("MPSP", StringComparison.Ordinal) => SearchTypes.Podcast,
        _ when browseId.StartsWith("MPED", StringComparison.Ordinal) => SearchTypes.Episode,
        _ => null,
    };

    /// <summary>Album browse ids start with MPRE; uploaded albums carry "release_detail".</summary>
    public static bool IsAlbum(string? browseId) =>
        browseId is not null
        && (browseId.StartsWith("MPRE", StringComparison.Ordinal) || browseId.Contains("release_detail", StringComparison.Ordinal));
}
