using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// Album page, <c>browse {"browseId": "MPRE..."}</c> (ytmusicapi get_album / parse_album_header_2024).
/// </summary>
internal static class AlbumParser
{
    private const string Page = "Album";

    public static AlbumPage Parse(JsonNode response, string browseId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var header = response.Obj("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "musicResponsiveHeaderRenderer")
            // Legacy header, still used for uploaded albums.
            ?? response.Obj("header", "musicDetailHeaderRenderer");
        if (header is null)
        {
            scope.MissingStructure("musicResponsiveHeaderRenderer (album header)");
        }

        var title = header.Str("title", "runs", 0, "text");
        if (header is not null && title is null)
        {
            scope.MissingStructure("album header title");
        }

        var subtitleRuns = header.Arr("subtitle", "runs");
        var subtitleInfo = TextRuns.ParseSongRuns(subtitleRuns, 2);
        var straplineRuns = header.Arr("straplineTextOne", "runs");
        var artists = straplineRuns is { Count: > 0 } ? TextRuns.ParseArtistsRuns(straplineRuns) : subtitleInfo.Artists;

        // "13 songs • 1 hour, 14 minutes", or just the duration.
        var secondSubtitle = header.Arr("secondSubtitle", "runs");
        int? trackCount = null;
        string? durationText;
        if (secondSubtitle is { Count: > 1 })
        {
            trackCount = TextRuns.ParseInt(secondSubtitle.Str(0, "text"));
            durationText = secondSubtitle.Str(2, "text");
        }
        else
        {
            durationText = secondSubtitle.Str(0, "text");
        }

        var album = new Album
        {
            Title = title ?? string.Empty,
            BrowseId = browseId,
            Thumbnails = Thumbnails.OfResponsive(header),
            Type = EnglishText.ParseAlbumType(subtitleRuns.Str(0, "text")) ?? AlbumType.Album,
            Year = subtitleInfo.Year,
            Artists = artists,
            IsExplicit = header.Str("subtitleBadges", 0, "musicInlineBadgeRenderer", "accessibilityData", "accessibilityData", "label") is not null,
            AudioPlaylistId = AudioPlaylistId(header),
        };

        var secondary = response.Arr("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents");
        var shelf = secondary.Objects().Select(s => s.Obj("musicShelfRenderer")).FirstOrDefault(s => s is not null);
        if (shelf is null)
        {
            scope.MissingStructure("secondaryContents.sectionListRenderer.contents[*].musicShelfRenderer (album tracks)");
        }

        // Album rows have no album column; ytmusicapi sets every track's album to the header title and
        // falls back to the album artists when a row has none.
        var albumRef = new AlbumRef(album.Title, browseId);
        var tracks = PlaylistItemParser.ParseRows(shelf.Arr("contents"), scope, isAlbum: true)
            .Select(t => t with { Album = albumRef, Artists = t.Artists.Count > 0 ? t.Artists : artists })
            .ToList();

        return new AlbumPage
        {
            Album = album,
            Description = DescriptionText(header),
            Tracks = tracks,
            TrackCount = trackCount,
            DurationText = durationText,
            OtherVersions = ParseOtherVersions(secondary, scope),
        };
    }

    /// <summary>
    /// Carousels under the track list are told apart by item size: MEDIUM = other versions of this album,
    /// SMALL = related recommendations (not modelled).
    /// </summary>
    private static List<Album> ParseOtherVersions(JsonArray? secondary, ParseScope scope)
    {
        var albums = new List<Album>();
        foreach (var carousel in secondary.Objects().Select(s => s.Obj("musicCarouselShelfRenderer")).OfType<JsonObject>())
        {
            if (carousel.Str("itemSize") != "COLLECTION_STYLE_ITEM_SIZE_MEDIUM")
            {
                continue;
            }

            foreach (var entry in carousel.Arr("contents").Objects())
            {
                if (entry.Obj(TwoRowItemParser.RendererName) is { } card)
                {
                    if (TwoRowItemParser.ParseAlbum(card, scope) is { } version)
                    {
                        albums.Add(version);
                    }
                }
                else
                {
                    scope.SkipItem(entry.Describe(), "other-versions item is not an album card");
                }
            }
        }

        return albums;
    }

    private static string? AudioPlaylistId(JsonObject? header)
    {
        foreach (var button in header.Arr("buttons").Objects())
        {
            if (button.Obj("musicPlayButtonRenderer", "playNavigationEndpoint") is { } play)
            {
                return play.Str("watchPlaylistEndpoint", "playlistId") ?? play.Str("watchEndpoint", "playlistId");
            }
        }

        // Legacy header: play button is the first top-level menu button.
        var legacy = header.Obj("menu", "menuRenderer", "topLevelButtons", 0, "buttonRenderer", "navigationEndpoint");
        return legacy.Str("watchPlaylistEndpoint", "playlistId") ?? legacy.Str("watchEndpoint", "playlistId");
    }

    private static string? DescriptionText(JsonObject? header)
    {
        var text = TextRuns.Text(header.Obj("description", "musicDescriptionShelfRenderer", "description"))
            ?? TextRuns.Text(header.Obj("description"));
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
