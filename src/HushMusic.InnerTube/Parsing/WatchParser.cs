using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// Up-next queue / radio, <c>next</c> endpoint (ytmusicapi get_watch_playlist / parse_watch_playlist).
/// </summary>
internal static class WatchParser
{
    private const string Page = "Watch";

    public static WatchPlaylist Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var tabs = response.Arr("contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer", "watchNextTabbedResultsRenderer", "tabs");
        if (tabs is null)
        {
            scope.MissingStructure("contents.singleColumnMusicWatchNextResultsRenderer.tabbedRenderer.watchNextTabbedResultsRenderer.tabs");
            return new WatchPlaylist();
        }

        string? lyricsBrowseId = null, relatedBrowseId = null;
        foreach (var tab in tabs.Objects().Select(t => t.Obj("tabRenderer")).OfType<JsonObject>())
        {
            // An "unselectable" lyrics tab means the track has no lyrics.
            if (tab.Has("unselectable"))
            {
                continue;
            }

            var endpoint = tab.Obj("endpoint");
            switch (PageTypes.Of(endpoint))
            {
                case PageTypes.TrackLyrics:
                    lyricsBrowseId = endpoint.Str("browseEndpoint", "browseId");
                    break;
                case PageTypes.TrackRelated:
                    relatedBrowseId = endpoint.Str("browseEndpoint", "browseId");
                    break;
            }
        }

        var panel = tabs.Obj(0, "tabRenderer", "content", "musicQueueRenderer", "content", "playlistPanelRenderer");
        if (panel is null)
        {
            // ytmusicapi: "No content returned" - e.g. a private or unavailable playlist.
            scope.MissingStructure("tabs[0].tabRenderer.content.musicQueueRenderer.content.playlistPanelRenderer");
            return new WatchPlaylist { LyricsBrowseId = lyricsBrowseId, RelatedBrowseId = relatedBrowseId };
        }

        var rows = panel.Arr("contents");
        return new WatchPlaylist
        {
            Tracks = ParseRows(rows, scope),
            PlaylistId = rows.Objects()
                .Select(r => PanelVideo(r).Str("navigationEndpoint", "watchEndpoint", "playlistId"))
                .FirstOrDefault(id => id is not null) ?? panel.Str("playlistId"),
            LyricsBrowseId = lyricsBrowseId,
            RelatedBrowseId = relatedBrowseId,
            Continuation = Continuations.Classic(panel),
        };
    }

    /// <summary>More queue items (radios are endless): <c>continuationContents.playlistPanelContinuation</c>.</summary>
    public static Paged<Track> ParseContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var panel = response.Obj("continuationContents", "playlistPanelContinuation");
        if (panel is null)
        {
            scope.MissingStructure("continuationContents.playlistPanelContinuation");
            return Paged<Track>.Empty;
        }

        return new Paged<Track>(ParseRows(panel.Arr("contents"), scope), Continuations.Classic(panel));
    }

    private static List<Track> ParseRows(JsonArray? rows, ParseScope scope)
    {
        var tracks = new List<Track>();
        foreach (var row in rows.Objects())
        {
            var video = PanelVideo(row);
            if (video is null)
            {
                // e.g. automixPreviewVideoRenderer at the end of a queue; ytmusicapi skips these too.
                scope.IgnoreItem(row.Describe(), "not a queue video");
                continue;
            }

            if (video.Has("unplayableText"))
            {
                scope.IgnoreItem("playlistPanelVideoRenderer", "unplayable");
                continue;
            }

            if (ParseTrack(video, scope) is { } track)
            {
                tracks.Add(track);
            }
        }

        return tracks;
    }

    /// <summary>
    /// The video of a queue row. Wrapped rows hold an audio/video twin in <c>counterpart</c>; only the primary
    /// renderer is used (the model has no counterpart field).
    /// </summary>
    private static JsonObject? PanelVideo(JsonObject row) =>
        row.Obj("playlistPanelVideoRenderer")
        ?? row.Obj("playlistPanelVideoWrapperRenderer", "primaryRenderer", "playlistPanelVideoRenderer");

    private static Track? ParseTrack(JsonObject video, ParseScope scope)
    {
        var videoId = video.Str("videoId");
        var title = TextRuns.Text(video.Obj("title"));
        if (videoId is null || title is null)
        {
            scope.SkipItem("playlistPanelVideoRenderer", "queue item without videoId or title");
            return null;
        }

        LikeStatus? likeStatus = null;
        foreach (var entry in Menus.Items(video).Objects())
        {
            if (entry.Obj("toggleMenuServiceItemRenderer", "defaultServiceEndpoint") is { } service && service.Has("likeEndpoint"))
            {
                likeStatus = Menus.ParseToggleLikeStatus(service);
            }
        }

        var navigation = video.Obj("navigationEndpoint");
        var info = TextRuns.ParseSongRuns(video.Arr("longBylineText", "runs"));
        return new Track
        {
            Title = title,
            VideoId = videoId,
            Duration = TextRuns.ParseDuration(video.Str("lengthText", "runs", 0, "text")),
            Thumbnails = Thumbnails.OfPlain(video),
            LikeStatus = likeStatus,
            Type = VideoTypes.ToTrackType(VideoTypes.Of(navigation), TrackType.Unknown),
            Artists = info.Artists,
            Album = info.Album,
            Views = info.Views,
            PlaylistId = navigation.Str("watchEndpoint", "playlistId"),
            // Per-entry id inside the queue; the same value as setVideoId when the queue is one of the user's playlists.
            SetVideoId = video.Str("playlistSetVideoId"),
            FeedbackTokens = Menus.LibraryTokens(video),
        };
    }
}
