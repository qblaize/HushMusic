using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>Home feed, <c>browse {"browseId": "FEmusic_home"}</c> (ytmusicapi get_home / parse_mixed_content).</summary>
internal static class HomeParser
{
    private const string Page = "Home";

    public static Paged<Shelf> Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var sectionList = response.Obj("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer");
        if (sectionList is null)
        {
            scope.MissingStructure("contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer");
            return Paged<Shelf>.Empty;
        }

        return new Paged<Shelf>(ShelfParser.ParseRows(sectionList.Arr("contents"), scope), Continuations.Classic(sectionList));
    }

    /// <summary>
    /// Next page of shelves. The response also carries a leftover top-level <c>contents</c> tab shell;
    /// only <c>continuationContents</c> is read.
    /// </summary>
    public static Paged<Shelf> ParseContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var continuation = response.Obj("continuationContents", "sectionListContinuation");
        if (continuation is null)
        {
            scope.MissingStructure("continuationContents.sectionListContinuation");
            return Paged<Shelf>.Empty;
        }

        return new Paged<Shelf>(ShelfParser.ParseRows(continuation.Arr("contents"), scope), Continuations.Classic(continuation));
    }
}
