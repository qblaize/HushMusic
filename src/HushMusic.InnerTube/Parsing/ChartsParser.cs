using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>Charts, <c>browse {"browseId": "FEmusic_charts", "formData": {"selectedValues": [country]}}</c> (ytmusicapi get_charts).</summary>
/// <remarks>
/// The carousels carry nothing machine-readable, so like ytmusicapi they are recognised by their contents: a carousel
/// whose first card links to a "VL" playlist is a playlist chart, the first carousel of list rows is the artist chart,
/// anything else (album charts in some regions, podcast shows) is passed over. ytmusicapi names the playlist carousels
/// by position and country ("videos", "genres", "daily"...); here they keep the title the page shows.
/// </remarks>
internal static class ChartsParser
{
    private const string Page = "Charts";

    public static ChartsPage Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        if (ExploreParser.SectionList(response) is not { Count: > 0 } sections)
        {
            scope.MissingStructure("contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents");
            return new ChartsPage();
        }

        // The country picker sits in the first section, an otherwise empty list shelf.
        var countryName = sections.Str(
            0, "musicShelfRenderer", "subheaders", 0, "musicSideAlignedItemRenderer", "startItems", 0,
            "musicSortFilterButtonRenderer", "title", "runs", 0, "text");
        if (countryName is null)
        {
            scope.MissingStructure("sectionListRenderer.contents[0].musicShelfRenderer country menu");
        }

        var playlistCharts = new List<Shelf>();
        IReadOnlyList<ChartEntry<Artist>>? topArtists = null;
        foreach (var section in sections.Objects().Skip(1))
        {
            if (section.Obj("musicCarouselShelfRenderer") is not { } carousel || carousel.Arr("contents") is not { Count: > 0 } contents)
            {
                continue;
            }

            if (IsPlaylistCarousel(contents))
            {
                var shelf = ShelfParser.ParseShelf(carousel, contents, scope);
                playlistCharts.Add(shelf with { Items = [.. shelf.Items.OfType<Playlist>()] });
            }
            else if (contents.Obj(0).Has("musicResponsiveListItemRenderer"))
            {
                topArtists ??= ParseArtists(contents, scope);
            }
            else
            {
                scope.IgnoreItem("musicCarouselShelfRenderer", "unknown chart carousel");
            }
        }

        return new ChartsPage
        {
            CountryName = countryName,
            Countries = ParseCountries(response),
            PlaylistCharts = playlistCharts,
            TopArtists = topArtists ?? [],
        };
    }

    // The country codes are not in the menu itself but in the form entities it refers to.
    private static List<string> ParseCountries(JsonNode response) =>
        [.. response.Arr("frameworkUpdates", "entityBatchUpdate", "mutations").Objects()
            .Select(mutation => mutation.Str("payload", "musicFormBooleanChoice", "opaqueToken"))
            .OfType<string>()];

    private static bool IsPlaylistCarousel(JsonArray contents) =>
        TextRuns.BrowseId(contents.Obj(0, TwoRowItemParser.RendererName, "title", "runs", 0)) is { } browseId
        && browseId.StartsWith("VL", StringComparison.Ordinal);

    /// <summary>ytmusicapi parse_chart_artist: name and subscribers from the flex columns, the link from the row.</summary>
    private static List<ChartEntry<Artist>> ParseArtists(JsonArray contents, ParseScope scope)
    {
        var artists = new List<ChartEntry<Artist>>();
        foreach (var entry in contents.Objects())
        {
            if (entry.Obj("musicResponsiveListItemRenderer") is not { } row)
            {
                scope.IgnoreItem(entry.Describe(), "chart item is not an artist row");
                continue;
            }

            var name = Columns.FlexText(row, 0);
            var browseId = row.Str("navigationEndpoint", "browseEndpoint", "browseId");
            if (name is null || browseId is null)
            {
                scope.SkipItem("musicResponsiveListItemRenderer", "chart artist without name or browseId");
                continue;
            }

            var artist = new Artist
            {
                Title = name,
                BrowseId = browseId,
                Subscribers = TextRuns.FirstToken(Columns.FlexText(row, 1)),
                Thumbnails = Thumbnails.OfResponsive(row),
            };
            var (rank, trend) = ChartRanking.Of(row);
            artists.Add(new ChartEntry<Artist>(artist, rank, trend));
        }

        return artists;
    }
}
