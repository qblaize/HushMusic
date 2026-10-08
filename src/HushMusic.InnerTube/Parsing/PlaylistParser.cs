using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// Playlist page, <c>browse {"browseId": "VL" + playlistId}</c>; liked songs are <c>VLLM</c>
/// (ytmusicapi get_playlist / parse_playlist_header_meta / parse_playlist_items).
/// </summary>
internal static class PlaylistParser
{
    private const string Page = "Playlist";

    public static PlaylistPage Parse(JsonNode response, string playlistId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var headerSection = response.Obj("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0);
        var editable = headerSection.Obj("musicEditablePlaylistDetailHeaderRenderer");
        var owned = editable is not null;
        var header = owned
            ? editable.Obj("header", "musicResponsiveHeaderRenderer")
            : headerSection.Obj("musicResponsiveHeaderRenderer") ?? response.Obj("header", "musicDetailHeaderRenderer");

        var shelf = response.Arr("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents")
            .Objects()
            .Select(s => s.Obj("musicPlaylistShelfRenderer"))
            .FirstOrDefault(s => s is not null);
        if (shelf is null)
        {
            scope.MissingStructure("secondaryContents.sectionListRenderer.contents[*].musicPlaylistShelfRenderer (tracks)");
        }

        var facepile = header.Obj("facepile", "avatarStackViewModel");
        var onTap = facepile.Obj("rendererContext", "commandContext", "onTap", "innertubeCommand");
        var isCollaborative = onTap.Str("showEngagementPanelEndpoint", "identifier", "tag") == "PAplaylist_collaborate";

        var rows = shelf.Arr("contents");
        var tracks = PlaylistItemParser.ParseRows(rows, scope, isCollaborative: isCollaborative);
        var tracksPage = new Paged<Track>(tracks, Continuations.FromItems(rows));

        var bareId = BrowseIds.StripVl(playlistId);
        if (header is null)
        {
            if (shelf is not null && bareId.StartsWith("OLA", StringComparison.Ordinal))
            {
                // Album audio playlists can come without a header: title from the first track's album (ytmusicapi parse_audio_playlist).
                return new PlaylistPage
                {
                    Playlist = new Playlist
                    {
                        PlaylistId = shelf.Str("targetId") ?? bareId,
                        Title = tracks.FirstOrDefault()?.Album?.Name ?? string.Empty,
                        TrackCount = tracks.Count,
                    },
                    Privacy = PrivacyStatus.Public,
                    Tracks = tracksPage,
                };
            }

            scope.MissingStructure("musicResponsiveHeaderRenderer (playlist header)");
        }

        // "11K views • 151 tracks • 15+ hours": views only when there are more than 3 runs, duration when more than 1.
        var second = header.Arr("secondSubtitle", "runs");
        int? trackCount = null;
        string? durationText = null;
        if (second is { Count: > 0 })
        {
            var viewsOffset = second.Count > 3 ? 2 : 0;
            trackCount = TextRuns.ParseInt(second.Str(viewsOffset, "text"));
            durationText = second.Count > 1 ? second.Str(viewsOffset + 2, "text") : null;
        }

        // Owned subtitle is "Playlist • Private • 2024": the privacy word shifts the year by two runs.
        var subtitleInfo = TextRuns.ParseSongRuns(header.Arr("subtitle", "runs"), owned ? 4 : 2);

        var playlist = new Playlist
        {
            PlaylistId = (owned ? editable.Str("playlistId") : null) ?? PlayButtonPlaylistId(header) ?? bareId,
            Title = TextRuns.Text(header.Obj("title")) ?? string.Empty,
            Thumbnails = Thumbnails.OfResponsive(header),
            Description = NullIfEmpty(TextRuns.Text(header.Obj("description", "musicDescriptionShelfRenderer", "description"))),
            Author = isCollaborative ? null : Author(header, facepile, onTap),
            TrackCount = trackCount,
        };

        return new PlaylistPage
        {
            Playlist = playlist,
            IsOwned = owned,
            Privacy = owned
                ? ParsePrivacy(editable.Str("editHeader", "musicPlaylistEditHeaderRenderer", "privacy"))
                : PrivacyStatus.Public,
            Year = subtitleInfo.Year,
            DurationText = durationText,
            Tracks = tracksPage,
        };
    }

    /// <summary>
    /// Next page of tracks. The request is <c>browse {"continuation": token}</c> and the rows come back in
    /// <c>onResponseReceivedActions[].appendContinuationItemsAction.continuationItems</c>, with the next token in
    /// a trailing <c>continuationItemRenderer</c>. The older <c>musicPlaylistShelfContinuation</c> shape is also accepted.
    /// </summary>
    public static Paged<Track> ParseContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        if (Continuations.AppendedItems(response) is { } items)
        {
            return new Paged<Track>(PlaylistItemParser.ParseRows(items, scope), Continuations.FromItems(items));
        }

        if (response.Obj("continuationContents", "musicPlaylistShelfContinuation") is { } shelf)
        {
            var rows = shelf.Arr("contents");
            return new Paged<Track>(PlaylistItemParser.ParseRows(rows, scope), Continuations.Classic(shelf) ?? Continuations.FromItems(rows));
        }

        scope.MissingStructure("onResponseReceivedActions[].appendContinuationItemsAction.continuationItems");
        return Paged<Track>.Empty;
    }

    private static ArtistRef? Author(JsonObject? header, JsonObject? facepile, JsonObject? onTap)
    {
        if (facepile.Str("text", "content") is { Length: > 0 } name)
        {
            return new ArtistRef(name, onTap.Str("browseEndpoint", "browseId"));
        }

        // Older headers name the owner in the strapline.
        var strapline = header.Obj("straplineTextOne", "runs", 0);
        return strapline.Str("text") is { Length: > 0 } owner ? new ArtistRef(owner, TextRuns.BrowseId(strapline)) : null;
    }

    private static string? PlayButtonPlaylistId(JsonObject? header)
    {
        foreach (var button in header.Arr("buttons").Objects())
        {
            if (button.Obj("musicPlayButtonRenderer", "playNavigationEndpoint") is { } play)
            {
                return play.Str("watchEndpoint", "playlistId") ?? play.Str("watchPlaylistEndpoint", "playlistId");
            }
        }

        return null;
    }

    private static PrivacyStatus? ParsePrivacy(string? value) => value switch
    {
        "PUBLIC" => PrivacyStatus.Public,
        "UNLISTED" => PrivacyStatus.Unlisted,
        "PRIVATE" => PrivacyStatus.Private,
        _ => null,
    };

    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;
}
