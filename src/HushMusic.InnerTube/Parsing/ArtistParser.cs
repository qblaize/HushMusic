using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// Artist page, <c>browse {"browseId": channelId}</c> (ytmusicapi get_artist).
/// </summary>
/// <remarks>
/// ytmusicapi keeps only carousels whose English title it knows (Albums, Singles &amp; EPs, Videos...) and drops
/// "Featured on", "Live performances", "Playlists by ...". Here every carousel becomes a <see cref="Shelf"/> in
/// page order, typed by its items' pageType, so no title matching is needed.
/// </remarks>
internal static class ArtistParser
{
    private const string Page = "Artist";

    public static ArtistPage Parse(JsonNode response, string channelId, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        // Some artist pages use the two-column layout (ytmusicapi #929).
        var sections = response.Arr("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents")
            ?? response.Arr("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");
        if (sections is null)
        {
            scope.MissingStructure("contents.*.tabs[0].tabRenderer.content.sectionListRenderer.contents");
        }

        var header = response.Obj("header", "musicImmersiveHeaderRenderer") ?? response.Obj("header").Renderer().Value;
        var name = header.Str("title", "runs", 0, "text");
        if (name is null)
        {
            scope.MissingStructure("header.musicImmersiveHeaderRenderer.title");
        }

        var artist = new Artist
        {
            Title = name ?? string.Empty,
            // The subscribe button's channelId can differ from the page id; the page id is what browses back here.
            BrowseId = BrowseIds.StripMpla(channelId),
            Thumbnails = Thumbnails.OfResponsive(header),
            Subscribers = header.Str("subscriptionButton", "subscribeButtonRenderer", "subscriberCountText", "runs", 0, "text"),
        };

        var radio = header.Obj("startRadioButton", "buttonRenderer", "navigationEndpoint");
        string? description = null, views = null, allSongsPlaylistId = null;
        IReadOnlyList<Track> topSongs = [];
        var shelves = new List<Shelf>();
        foreach (var section in sections.Objects())
        {
            var (rendererName, renderer) = section.Renderer();
            switch (rendererName)
            {
                case "musicDescriptionShelfRenderer" when description is null:
                    description = TextRuns.Text(renderer.Obj("description"));
                    views = renderer.Str("subheader", "runs", 0, "text");
                    break;

                case "musicShelfRenderer" when topSongs.Count == 0:
                    // "Top songs"; the title links to the full list as a VL playlist.
                    topSongs = PlaylistItemParser.ParseRows(renderer.Arr("contents"), scope);
                    allSongsPlaylistId = renderer.Str("title", "runs", 0, "navigationEndpoint", "browseEndpoint", "browseId") is { } songsId
                        ? BrowseIds.StripVl(songsId)
                        : null;
                    break;

                default:
                    if (ShelfParser.ParseRow(section, scope) is { } shelf)
                    {
                        shelves.Add(shelf);
                    }

                    break;
            }
        }

        return new ArtistPage
        {
            Artist = artist,
            Description = string.IsNullOrEmpty(description) ? null : description,
            Views = views,
            ShufflePlaylistId = header.Str("playButton", "buttonRenderer", "navigationEndpoint", "watchEndpoint", "playlistId"),
            // The artist's own channel page links the radio with a watchPlaylistEndpoint.
            RadioPlaylistId = radio.Str("watchEndpoint", "playlistId") ?? radio.Str("watchPlaylistEndpoint", "playlistId"),
            TopSongs = topSongs,
            AllSongsPlaylistId = allSongsPlaylistId,
            Sections = shelves,
        };
    }

    /// <summary>
    /// The artist's full discography grid, <c>browse {"browseId": "MPAD...", "params"}</c> taken from a section's
    /// <see cref="Shelf.MoreBrowseId"/>/<see cref="Shelf.MoreParams"/> (ytmusicapi get_artist_albums / parse_albums).
    /// Continuation responses use <c>continuationContents.gridContinuation</c>, see <see cref="LibraryParser.ParseAlbumsContinuation"/>.
    /// </summary>
    public static Paged<Album> ParseDiscography(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, "ArtistDiscography");

        var first = response.Obj("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0);
        var grid = first.Obj("gridRenderer");
        var items = grid.Arr("items") ?? first.Arr("musicCarouselShelfRenderer", "contents");
        if (items is null)
        {
            scope.MissingStructure("sectionListRenderer.contents[0].gridRenderer.items");
            return Paged<Album>.Empty;
        }

        return new Paged<Album>(LibraryParser.ParseAlbumCards(items, scope), Continuations.Classic(grid));
    }
}
