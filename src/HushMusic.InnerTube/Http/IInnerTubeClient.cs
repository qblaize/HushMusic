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
}
