using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>Search results, <c>search {"query", "params"?}</c> (ytmusicapi SearchMixin.search).</summary>
/// <remarks>
/// Shelves are built from consecutive results with the same category. Since 2026 the unfiltered page is a flat
/// list of untitled single-item sections, so it comes back as one shelf with an empty title; a filtered search
/// returns one titled shelf ("Songs"). Continuation pages return their items in one untitled shelf.
/// </remarks>
internal static class SearchParser
{
    private const string Page = "Search";

    public static SearchResults Parse(JsonNode response, SearchFilter filter, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var contents = response.Obj("contents");
        if (contents is null)
        {
            // ytmusicapi: no "contents" means no results.
            scope.IgnoreItem("response", "no contents (no results)");
            return new SearchResults();
        }

        var sectionList = contents.Arr("tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents")
            ?? contents.Arr("sectionListRenderer", "contents");
        if (sectionList is null)
        {
            scope.MissingStructure("contents.tabbedSearchResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents");
            return new SearchResults();
        }

        var filterType = ResultTypeOf(filter);
        MediaItem? topResult = null;
        string? continuation = null;
        var builder = new ShelfBuilder();

        foreach (var section in sectionList.Objects())
        {
            var (name, renderer) = section.Renderer();
            if (renderer is null)
            {
                scope.SkipItem(section.Describe(), "search section is not a renderer object");
                continue;
            }

            switch (name)
            {
                case "musicCardShelfRenderer":
                {
                    topResult ??= SearchItemParser.ParseTopResult(renderer, scope);
                    var cardContents = renderer.Arr("contents");
                    string? cardCategory = null;
                    var rows = cardContents.Objects().ToList();
                    if (rows.FirstOrDefault()?.Obj("messageRenderer") is { } message)
                    {
                        // "More from YouTube" style header row: becomes the category of the rows after it.
                        cardCategory = message.Str("text", "runs", 0, "text");
                        rows.RemoveAt(0);
                    }

                    builder.Add(cardCategory, ParseRows(rows, null, scope));
                    break;
                }

                case "musicShelfRenderer":
                {
                    var category = renderer.Str("title", "runs", 0, "text");
                    if (filterType is not null && category is not null && !EnglishText.ShelfMatchesType(category, filterType))
                    {
                        scope.IgnoreItem(name, $"shelf \"{category}\" does not match the {filterType} filter");
                        continue;
                    }

                    builder.Add(category, ParseRows(renderer.Arr("contents").Objects(), filterType, scope));
                    if (filterType is not null)
                    {
                        continuation ??= Continuations.Classic(renderer);
                    }

                    break;
                }

                case "itemSectionRenderer":
                    // Single results in the 2026 layout, or the "About these results" messageRenderer (skipped).
                    builder.Add(null, ParseRows(renderer.Arr("contents").Objects(), filterType, scope, ignoreNonRows: true));
                    break;

                default:
                    scope.IgnoreItem(name ?? "?", "unsupported search section");
                    break;
            }
        }

        return new SearchResults { TopResult = topResult, Sections = builder.Build(), Continuation = continuation };
    }

    /// <summary>
    /// Next page of a filtered search (<c>continuationContents.musicShelfContinuation</c>). The response also has
    /// a leftover top-level <c>contents</c> with only the "About these results" message; it is ignored.
    /// </summary>
    public static SearchResults ParseContinuation(JsonNode response, SearchFilter filter, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var shelf = response.Obj("continuationContents", "musicShelfContinuation");
        if (shelf is null)
        {
            scope.MissingStructure("continuationContents.musicShelfContinuation");
            return new SearchResults();
        }

        var items = ParseRows(shelf.Arr("contents").Objects(), ResultTypeOf(filter), scope);
        return new SearchResults
        {
            Sections = items.Count == 0 ? [] : [new Shelf { Title = string.Empty, Items = items }],
            Continuation = Continuations.Classic(shelf),
        };
    }

    /// <summary>The singular result type a filter forces on every row (ytmusicapi uses the filter name minus "s").</summary>
    private static string? ResultTypeOf(SearchFilter filter) => filter switch
    {
        SearchFilter.Songs => SearchTypes.Song,
        SearchFilter.Videos => SearchTypes.Video,
        SearchFilter.Albums => SearchTypes.Album,
        SearchFilter.Artists => SearchTypes.Artist,
        SearchFilter.CommunityPlaylists or SearchFilter.FeaturedPlaylists => SearchTypes.Playlist,
        _ => null,
    };

    private static List<MediaItem> ParseRows(IEnumerable<JsonObject> rows, string? resultType, ParseScope scope, bool ignoreNonRows = false)
    {
        var items = new List<MediaItem>();
        foreach (var row in rows)
        {
            if (row.Obj("musicResponsiveListItemRenderer") is { } item)
            {
                if (SearchItemParser.ParseRow(item, resultType, scope) is { } parsed)
                {
                    items.Add(parsed);
                }
            }
            else if (ignoreNonRows)
            {
                scope.IgnoreItem(row.Describe(), "not a result row");
            }
            else
            {
                scope.SkipItem(row.Describe(), "not a result row");
            }
        }

        return items;
    }

    /// <summary>Groups consecutive results with the same category into shelves.</summary>
    private sealed class ShelfBuilder
    {
        private readonly List<(string? Category, List<MediaItem> Items)> _groups = [];

        public void Add(string? category, List<MediaItem> items)
        {
            if (items.Count == 0)
            {
                return;
            }

            if (_groups.Count > 0 && _groups[^1].Category == category)
            {
                _groups[^1].Items.AddRange(items);
            }
            else
            {
                _groups.Add((category, items));
            }
        }

        public List<Shelf> Build() =>
            _groups.Select(g => new Shelf { Title = g.Category ?? string.Empty, Items = g.Items }).ToList();
    }
}
