using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// "Related" tab of the player, <c>browse {"browseId": "MPTR..."}</c> (ytmusicapi get_song_related / parse_mixed_content).
/// </summary>
/// <remarks>
/// Shelves seen: "You might also like" and "Other performances" (song rows, <see cref="ShelfLayout.List"/>),
/// "Recommended playlists", "Similar artists", the artist's albums (titled with the artist name) and
/// "About the artist". That last one is text only, as in ytmusicapi: no items, the description is in
/// <see cref="Shelf.Subtitle"/>.
/// </remarks>
internal static class RelatedParser
{
    private const string Page = "Related";

    public static IReadOnlyList<Shelf> Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var rows = response.Arr("contents", "sectionListRenderer", "contents");
        if (rows is null)
        {
            scope.MissingStructure("contents.sectionListRenderer.contents");
            return [];
        }

        return ShelfParser.ParseRows(rows, scope);
    }
}
