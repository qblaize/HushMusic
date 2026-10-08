using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// <c>music/get_search_suggestions {"input"}</c> (ytmusicapi parse_search_suggestions).
/// Text suggestions come from the first section; the entity rows of the second section (artist, songs,
/// videos), which ytmusicapi ignores, are parsed with the search row parser into <see cref="SearchSuggestions.Items"/>.
/// </summary>
internal static class SearchSuggestionsParser
{
    private const string Page = "SearchSuggestions";

    public static SearchSuggestions Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var sections = response.Arr("contents");
        if (sections is null)
        {
            // No suggestions for this input.
            scope.IgnoreItem("response", "no contents");
            return SearchSuggestions.Empty;
        }

        var queries = new List<string>();
        var items = new List<MediaItem>();
        foreach (var section in sections.Objects())
        {
            foreach (var entry in section.Arr("searchSuggestionsSectionRenderer", "contents").Objects())
            {
                var (name, renderer) = entry.Renderer();
                switch (name)
                {
                    // historySuggestionRenderer = a past search of the signed-in user (removable with its feedbackToken).
                    case "searchSuggestionRenderer" or "historySuggestionRenderer":
                        if ((renderer.Str("navigationEndpoint", "searchEndpoint", "query") ?? TextRuns.Text(renderer.Obj("suggestion"))) is { Length: > 0 } query)
                        {
                            queries.Add(query);
                        }
                        else
                        {
                            scope.SkipItem(name, "suggestion without query text");
                        }

                        break;

                    case "musicResponsiveListItemRenderer" when renderer is not null:
                        if (SearchItemParser.ParseRow(renderer, null, scope) is { } item)
                        {
                            items.Add(item);
                        }

                        break;

                    default:
                        scope.SkipItem(entry.Describe(), "unsupported suggestion renderer");
                        break;
                }
            }
        }

        return new SearchSuggestions(queries, items);
    }
}
