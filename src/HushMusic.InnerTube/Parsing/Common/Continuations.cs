using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Raw continuation tokens. InnerTube uses two styles (ytmusicapi continuations.py):
/// classic tokens on the renderer (sent back as <c>&amp;ctoken=T&amp;continuation=T</c>) and 2025-style
/// <c>continuationItemRenderer</c> entries at the end of an item list (sent back as body <c>{"continuation": T}</c>).
/// </summary>
internal static class Continuations
{
    /// <summary>
    /// Classic token at <c>renderer.continuations[0].next[Radio]ContinuationData.continuation</c>.
    /// Watch queues for radios (playlist ids other than PL/OLA) use <c>nextRadioContinuationData</c>.
    /// </summary>
    public static string? Classic(JsonNode? renderer) =>
        renderer.Str("continuations", 0, "nextContinuationData", "continuation")
        ?? renderer.Str("continuations", 0, "nextRadioContinuationData", "continuation");

    /// <summary>2025-style token from the trailing <c>continuationItemRenderer</c> of an item list.</summary>
    public static string? FromItems(JsonArray? items)
    {
        var last = items.Obj(-1)?.Obj("continuationItemRenderer");
        if (last is null)
        {
            return null;
        }

        if (last.Str("continuationEndpoint", "continuationCommand", "token") is { } token)
        {
            return token;
        }

        // The token can also be nested in a commandExecutorCommand next to other commands.
        foreach (var command in last.Arr("continuationEndpoint", "commandExecutorCommand", "commands").Objects())
        {
            if (command.Str("continuationCommand", "request") == "CONTINUATION_REQUEST_TYPE_BROWSE"
                && command.Str("continuationCommand", "token") is { } nested)
            {
                return nested;
            }
        }

        return null;
    }

    /// <summary>Items of a 2025-style continuation response (<c>appendContinuationItemsAction.continuationItems</c>).</summary>
    public static JsonArray? AppendedItems(JsonNode response)
    {
        foreach (var action in response.Arr("onResponseReceivedActions").Objects())
        {
            if (action.Arr("appendContinuationItemsAction", "continuationItems") is { } items)
            {
                return items;
            }
        }

        return null;
    }
}
