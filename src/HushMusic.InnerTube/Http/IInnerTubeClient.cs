using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Http;

/// <summary>Low-level InnerTube transport: context, headers, authentication, retries and error mapping.</summary>
internal interface IInnerTubeClient
{
    /// <summary>
    /// Sends <paramref name="request"/> and returns the parsed JSON response.
    /// Throws <see cref="Core.InnerTubeException"/> on failure and <see cref="Core.AuthRequiredException"/>
    /// when signed out or when YouTube Music rejects the stored session.
    /// </summary>
    Task<JsonNode> PostAsync(InnerTubeRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Authenticated GET of a YouTube playback-tracking URL (s.youtube.com), as ytmusicapi's
    /// <c>add_history_item</c> does. Only https *.youtube.com URLs are allowed.
    /// </summary>
    Task SendTrackingPingAsync(Uri url, CancellationToken cancellationToken);

    /// <summary>
    /// Anonymous GET of a public YouTube JSON resource (a caption track on www.youtube.com/api/timedtext): web headers
    /// and the consent cookie, never the account. Only https *.youtube.com URLs are allowed. Returns null when the body
    /// is empty (how YouTube refuses a caption request it won't serve); throws <see cref="Core.InnerTubeException"/> on
    /// an HTTP error.
    /// </summary>
    Task<JsonNode?> GetPublicJsonAsync(Uri url, string label, CancellationToken cancellationToken);
}
