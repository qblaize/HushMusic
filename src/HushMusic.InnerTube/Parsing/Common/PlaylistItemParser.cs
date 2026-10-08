using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Track rows of playlists, albums, artist "top songs", library songs and history
/// (ytmusicapi parse_playlist_items / parse_playlist_item).
/// </summary>
internal static class PlaylistItemParser
{
    public const string RendererName = "musicResponsiveListItemRenderer";

    /// <summary>Parses every row; trailing <c>continuationItemRenderer</c> entries are not rows and are skipped silently.</summary>
    public static List<Track> ParseRows(JsonArray? rows, ParseScope scope, bool isAlbum = false, bool isCollaborative = false) =>
        ParseRows(rows.Objects(), scope, isAlbum, isCollaborative);

    public static List<Track> ParseRows(IEnumerable<JsonObject> rows, ParseScope scope, bool isAlbum = false, bool isCollaborative = false)
    {
        var tracks = new List<Track>();
        foreach (var row in rows)
        {
            if (row.Obj(RendererName) is { } item)
            {
                if (Parse(item, scope, isAlbum, isCollaborative) is { } track)
                {
                    tracks.Add(track);
                }
            }
            else if (!row.Has("continuationItemRenderer"))
            {
                scope.SkipItem(row.Describe(), "not a track row");
            }
        }

        return tracks;
    }

    public static Track? Parse(JsonObject row, ParseScope scope, bool isAlbum = false, bool isCollaborative = false)
    {
        string? videoId = null, setVideoId = null;
        foreach (var entry in Menus.Items(row).Objects())
        {
            // Owned playlists: the "Remove from playlist" item carries the per-entry setVideoId.
            var edit = entry.Obj("menuServiceItemRenderer", "serviceEndpoint", "playlistEditEndpoint", "actions", 0);
            if (edit is not null)
            {
                setVideoId = edit.Str("setVideoId");
                videoId = edit.Str("removedVideoId");
            }
        }

        var play = row.Obj("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint");
        LikeStatus? likeStatus = null;
        if (play is not null)
        {
            videoId = play.Str("watchEndpoint", "videoId") ?? videoId;
            likeStatus = Menus.ParseLikeStatus(row.Str("menu", "menuRenderer", "topLevelButtons", 0, "likeButtonRenderer", "likeStatus"));
        }

        // Greyed-out rows have no play button or menu; ytmusicapi returns videoId None for them.
        // playlistItemData still carries the id, and Track.VideoId is required, so fall back to it.
        videoId ??= row.Str("playlistItemData", "videoId");

        var isAvailable = IsAvailable(row);
        var columns = FindColumns(row, presetColumns: !isAvailable || isAlbum, isCollaborative);

        // ytmusicapi leaves the title empty when no column links to a watch endpoint; the first column is the title.
        var title = Columns.FlexText(row, columns.Title ?? 0);
        if (title == EnglishText.SongDeleted)
        {
            scope.IgnoreItem(RendererName, "deleted song");
            return null;
        }

        if (title is null || videoId is null)
        {
            scope.SkipItem(RendererName, title is null ? "track row without a title column" : $"track row \"{title}\" without videoId");
            return null;
        }

        var durationText = columns.Duration is { } durationIndex ? Columns.FlexText(row, durationIndex) : null;
        if (row.Has("fixedColumns"))
        {
            durationText = Columns.FixedText(row, 0);
        }

        var albumRun = columns.Album is { } albumIndex ? Columns.FlexRun(row, albumIndex) : null;
        var videoType = Menus.Items(row).Nav(0, "menuNavigationItemRenderer", "navigationEndpoint") is { } startMix
            ? VideoTypes.Of(startMix)
            : null;
        videoType ??= VideoTypes.Of(play) ?? VideoTypes.Of(Columns.FlexRun(row, columns.Title ?? 0).Obj("navigationEndpoint"));

        return new Track
        {
            Title = title,
            VideoId = videoId,
            Artists = columns.Artist is { } artistIndex ? TextRuns.ParseArtistsRuns(Columns.FlexRuns(row, artistIndex)) : [],
            Album = albumRun?.Str("text") is { } albumName ? new AlbumRef(albumName, TextRuns.BrowseId(albumRun)) : null,
            Duration = TextRuns.ParseDuration(durationText),
            Thumbnails = Thumbnails.OfResponsive(row),
            Type = VideoTypes.ToTrackType(videoType, TrackType.Unknown),
            IsExplicit = HasBadge(row),
            IsAvailable = isAvailable,
            LikeStatus = likeStatus,
            // Rows without an edit menu still have playlistItemData.playlistSetVideoId: a stable per-row id
            // (videoIds repeat inside albums and playlists).
            SetVideoId = setVideoId ?? row.Str("playlistItemData", "playlistSetVideoId"),
            PlaylistId = play.Str("watchEndpoint", "playlistId"),
            // Album rows show the play count in the third column ("54M plays").
            Views = isAlbum ? Columns.FlexText(row, 2) : null,
            FeedbackTokens = Menus.LibraryTokens(row),
        };
    }

    /// <summary>Explicit badge on list rows (<c>.badges[0]...label</c> exists; the label text is not checked).</summary>
    public static bool HasBadge(JsonObject row) =>
        row.Str("badges", 0, "musicInlineBadgeRenderer", "accessibilityData", "accessibilityData", "label") is not null;

    public static bool IsAvailable(JsonObject row) =>
        row.Str("musicItemRendererDisplayPolicy") != "MUSIC_ITEM_RENDERER_DISPLAY_POLICY_GREY_OUT";

    /// <summary>
    /// Which flex column holds what. Album rows and greyed-out rows use fixed positions; otherwise each
    /// column is recognized by the endpoint of its first run.
    /// </summary>
    private static ColumnLayout FindColumns(JsonObject row, bool presetColumns, bool isCollaborative)
    {
        int? title = presetColumns ? 0 : null;
        int? artist = presetColumns ? 1 : null;
        // Collaborative playlists show the duration between artist and album.
        int? album = isCollaborative ? 3 : presetColumns ? 2 : null;
        int? duration = null, unrecognized = null, lastUserChannel = null;

        var count = Columns.FlexCount(row);
        for (var i = 0; i < count; i++)
        {
            var run = Columns.FlexRun(row, i);
            var endpoint = run.Obj("navigationEndpoint");
            if (endpoint is null)
            {
                if (run.Str("text") is { } text)
                {
                    if (TextRuns.IsDurationText(text))
                    {
                        duration = i;
                    }
                    else
                    {
                        unrecognized ??= i;
                    }
                }

                continue;
            }

            if (endpoint.Has("watchEndpoint"))
            {
                title = i;
            }
            else if (endpoint.Has("browseEndpoint"))
            {
                switch (PageTypes.Of(endpoint))
                {
                    // ARTIST for regular songs, UNKNOWN for uploads.
                    case PageTypes.Artist or PageTypes.Unknown:
                        artist = i;
                        break;
                    case PageTypes.Album or PageTypes.Audiobook:
                        album = i;
                        break;
                    case PageTypes.UserChannel:
                        lastUserChannel = i;
                        break;
                    case PageTypes.NonMusicAudioTrack:
                        // Podcast episodes link their title to the episode page.
                        title = i;
                        break;
                }
            }
        }

        // Rare songs have an unlinked artist; for non-music videos the uploader channel is the artist.
        artist ??= unrecognized ?? lastUserChannel;
        return new ColumnLayout(title, artist, album, duration);
    }

    private readonly record struct ColumnLayout(int? Title, int? Artist, int? Album, int? Duration);
}
