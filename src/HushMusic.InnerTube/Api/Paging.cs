using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>
/// Legacy query-string continuations (ytmusicapi <c>get_continuations</c>): the next page is the same
/// endpoint and body with <c>&amp;ctoken=T&amp;continuation=T</c> appended.
/// </summary>
internal static class QueryPaging
{
    public static async Task<Paged<T>> GetAsync<T>(
        IInnerTubeClient client,
        string endpoint,
        string scope,
        JsonObject firstPageBody,
        string? continuation,
        bool requiresAuth,
        Func<JsonNode, Paged<T>> parseFirstPage,
        Func<JsonNode, Paged<T>> parseContinuation,
        CancellationToken cancellationToken)
    {
        if (continuation is null)
        {
            var response = await client.PostAsync(
                new InnerTubeRequest(endpoint, firstPageBody) { RequiresAuth = requiresAuth },
                cancellationToken).ConfigureAwait(false);
            var page = parseFirstPage(response);
            return page with { Continuation = ContinuationToken.Wrap(scope, page.Continuation, firstPageBody) };
        }

        var state = ContinuationToken.Unwrap(continuation, scope);
        var body = state.RequireBody();
        var next = await client.PostAsync(
            new InnerTubeRequest(endpoint, body) { QueryContinuation = state.Token, RequiresAuth = requiresAuth },
            cancellationToken).ConfigureAwait(false);
        var nextPage = parseContinuation(next);

        // ytmusicapi stops paging when a continuation page parses to zero items.
        return nextPage with
        {
            Continuation = nextPage.Items.Count == 0 ? null : ContinuationToken.Wrap(scope, nextPage.Continuation, body),
        };
    }
}

/// <summary>
/// Playlist pages (also liked songs = "LM"). Track paging uses the 2025 body-style continuation:
/// <c>browse {"continuation": T}</c> with no browseId and nothing in the query string.
/// </summary>
internal static class PlaylistPaging
{
    public static async Task<PlaylistPage> GetPageAsync(
        IInnerTubeClient client,
        string playlistId,
        bool requiresAuth,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        var browseId = playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId : "VL" + playlistId;
        var response = await client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["browseId"] = browseId }) { RequiresAuth = requiresAuth },
            cancellationToken).ConfigureAwait(false);

        var page = PlaylistParser.Parse(response, browseId[2..], logger);
        return page with
        {
            Tracks = page.Tracks with { Continuation = ContinuationToken.Wrap(ContinuationScope.Playlist, page.Tracks.Continuation) },
        };
    }

    public static async Task<Paged<Track>> GetTracksAsync(
        IInnerTubeClient client,
        string continuation,
        bool requiresAuth,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var state = ContinuationToken.Unwrap(continuation, ContinuationScope.Playlist);
        var response = await client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["continuation"] = state.Token }) { RequiresAuth = requiresAuth },
            cancellationToken).ConfigureAwait(false);

        var page = PlaylistParser.ParseContinuation(response, logger);
        return page with
        {
            Continuation = page.Items.Count == 0 ? null : ContinuationToken.Wrap(ContinuationScope.Playlist, page.Continuation),
        };
    }

    /// <summary>ytmusicapi <c>validate_playlist_id</c>: edit/next endpoints take the id without "VL".</summary>
    public static string StripBrowsePrefix(string playlistId) =>
        playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId[2..] : playlistId;
}
