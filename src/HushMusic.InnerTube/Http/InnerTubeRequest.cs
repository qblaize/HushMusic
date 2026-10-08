using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Http;

/// <summary>One POST to <c>https://music.youtube.com/youtubei/v1/{Endpoint}</c>.</summary>
internal sealed class InnerTubeRequest(string endpoint, JsonObject body)
{
    /// <summary>Endpoint path, e.g. <c>browse</c>, <c>music/get_search_suggestions</c>, <c>like/like</c>.</summary>
    public string Endpoint { get; } = endpoint;

    /// <summary>Endpoint fields. The client adds <c>context</c>; this object is never modified.</summary>
    public JsonObject Body { get; } = body;

    /// <summary>Raw ctoken for query-string continuations (sent as <c>&amp;ctoken=T&amp;continuation=T</c>).</summary>
    public string? QueryContinuation { get; init; }

    /// <summary>Throw <see cref="Core.AuthRequiredException"/> before sending when signed out (or when <see cref="Anonymous"/>).</summary>
    public bool RequiresAuth { get; init; }

    /// <summary>False for non-idempotent writes, which must not be repeated after a 5xx or network error.</summary>
    public bool AllowRetry { get; init; } = true;

    /// <summary>Client sent in <c>context.client</c>; only this request is affected.</summary>
    public ClientProfile Client { get; init; } = ClientProfile.Web;

    /// <summary>
    /// Send without the account even when signed in, exactly like ytmusicapi's unauthenticated <c>YTMusic()</c>:
    /// no credentials, no <c>&amp;key=</c>, and the response never touches the session (no cookie rotation,
    /// no "session expired"). For data that is the same for every user.
    /// </summary>
    public bool Anonymous { get; init; }

    /// <summary>
    /// Extra check run on successful authenticated responses. Returns a reason when the response is
    /// really a signed-out page. The default sign-in-prompt check always runs as well.
    /// </summary>
    public Func<JsonNode, string?>? SignedOutCheck { get; init; }
}
