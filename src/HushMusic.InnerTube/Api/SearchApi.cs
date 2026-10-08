using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

internal sealed class SearchApi(IInnerTubeClient client, ILogger<SearchApi> logger) : ISearchApi
{
    public async Task<SearchResults> SearchAsync(string query, SearchFilter filter = SearchFilter.All, string? continuation = null, CancellationToken cancellationToken = default)
    {
        if (continuation is not null)
        {
            return await SearchContinuationAsync(continuation, cancellationToken).ConfigureAwait(false);
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            return new SearchResults();
        }

        var body = new JsonObject { ["query"] = query };
        if (GetSearchParams(filter) is { } searchParams)
        {
            body["params"] = searchParams;
        }

        var response = await client.PostAsync(new InnerTubeRequest("search", body), cancellationToken).ConfigureAwait(false);
        var results = SearchParser.Parse(response, filter, logger);

        // ytmusicapi only follows continuations for filtered searches.
        var next = filter == SearchFilter.All
            ? null
            : ContinuationToken.Wrap(ContinuationScope.Search, results.Continuation, body, filter.ToString());
        return results with { Continuation = next };
    }

    public async Task<SearchSuggestions> GetSuggestionsAsync(string input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return SearchSuggestions.Empty;
        }

        var response = await client.PostAsync(
            new InnerTubeRequest("music/get_search_suggestions", new JsonObject { ["input"] = input }),
            cancellationToken).ConfigureAwait(false);
        return SearchSuggestionsParser.Parse(response, logger);
    }

    /// <summary>
    /// ytmusicapi <c>parsers/search.py:get_search_params</c> for scope = None, ignore_spelling = False.
    /// The values contain a literal "%3D" that must be sent as-is.
    /// </summary>
    internal static string? GetSearchParams(SearchFilter filter) => filter switch
    {
        SearchFilter.All => null,
        SearchFilter.Songs => "EgWKAQIIAWoMEA4QChADEAQQCRAF",
        SearchFilter.Videos => "EgWKAQIQAWoMEA4QChADEAQQCRAF",
        SearchFilter.Albums => "EgWKAQIYAWoMEA4QChADEAQQCRAF",
        SearchFilter.Artists => "EgWKAQIgAWoMEA4QChADEAQQCRAF",
        SearchFilter.CommunityPlaylists => "EgeKAQQoAEABagwQDhAKEAMQBBAJEAU%3D",
        SearchFilter.FeaturedPlaylists => "EgeKAQQoADgBagwQDhAKEAMQBBAJEAU%3D",
        _ => throw new ArgumentOutOfRangeException(nameof(filter), filter, "Unknown search filter."),
    };

    private async Task<SearchResults> SearchContinuationAsync(string continuation, CancellationToken cancellationToken)
    {
        var state = ContinuationToken.Unwrap(continuation, ContinuationScope.Search);
        if (!Enum.TryParse<SearchFilter>(state.Filter, out var filter))
        {
            throw new ArgumentException("The search continuation token has no filter.", nameof(continuation));
        }

        var body = state.RequireBody();
        var response = await client.PostAsync(
            new InnerTubeRequest("search", body) { QueryContinuation = state.Token },
            cancellationToken).ConfigureAwait(false);
        var results = SearchParser.ParseContinuation(response, filter, logger);

        var next = results.AllItems.Any()
            ? ContinuationToken.Wrap(ContinuationScope.Search, results.Continuation, body, state.Filter)
            : null;
        return results with { Continuation = next };
    }
}
