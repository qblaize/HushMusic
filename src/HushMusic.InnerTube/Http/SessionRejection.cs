using System.Text.Json.Nodes;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Http;

/// <summary>Recognizes responses in which YouTube Music treats the request as signed out.</summary>
internal static class SessionRejection
{
    public const string ExpiredMessage = "Your YouTube Music session has expired. Sign in again.";

    /// <summary>
    /// HTTP 401, or an error body such as "You must be signed in to perform this operation." /
    /// "Unauthorized. You must be signed in." (ytmusicapi issues #676, #962).
    /// </summary>
    public static bool IsSignedOutError(int statusCode, string? errorMessage) =>
        statusCode == 401
        || (errorMessage is not null && errorMessage.Contains("must be signed in", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Signed-out library pages (history, liked songs, library tabs) come back as HTTP 200 with a
    /// <c>messageRenderer</c> whose button opens a <c>signInEndpoint</c> instead of the requested shelf
    /// (ytmusicapi #1016). Returns the prompt text, or null when the page is not a sign-in prompt.
    /// Verified against anonymous FEmusic_history / FEmusic_liked_* / VLLM responses (2026-10-07).
    /// </summary>
    public static string? FindSignInPrompt(JsonNode response) =>
        FindMessagePrompt(response)
        ?? (SignInDetector.LooksSignedOut(response) ? "sign-in prompt instead of content" : null);

    private static string? FindMessagePrompt(JsonNode response)
    {
        if (JsonLookup.Get(
                response,
                "contents",
                "singleColumnBrowseResultsRenderer",
                "tabs",
                0,
                "tabRenderer",
                "content",
                "sectionListRenderer",
                "contents") is not JsonArray sections)
        {
            return null;
        }

        foreach (var section in sections)
        {
            if (FromMessage(JsonLookup.Get(section, "messageRenderer")) is { } direct)
            {
                return direct;
            }

            if (JsonLookup.Get(section, "itemSectionRenderer", "contents") is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (FromMessage(JsonLookup.Get(item, "messageRenderer")) is { } nested)
                    {
                        return nested;
                    }
                }
            }
        }

        return null;
    }

    private static string? FromMessage(JsonNode? message)
    {
        if (message is null || !JsonLookup.ContainsKey(message, "signInEndpoint"))
        {
            return null;
        }

        var text = JsonLookup.Get(message, "text", "runs") is JsonArray runs
            ? string.Concat(runs.Select(r => JsonLookup.GetString(r, "text")))
            : null;
        return string.IsNullOrWhiteSpace(text) ? "sign-in prompt instead of content" : text;
    }
}
