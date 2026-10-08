using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Api;

/// <summary>Which API method a continuation token belongs to.</summary>
internal static class ContinuationScope
{
    public const string Home = "home";
    public const string Search = "search";
    public const string Watch = "watch";
    public const string Playlist = "playlist";
    public const string LibraryPlaylists = "library.playlists";
    public const string LibrarySongs = "library.songs";
    public const string LibraryAlbums = "library.albums";
    public const string LibraryArtists = "library.artists";
    public const string ArtistAlbums = "artist.albums";
}

/// <summary>Decoded opaque continuation.</summary>
internal sealed record ContinuationState(string Scope, string Token, JsonObject? Body, string? Filter)
{
    /// <summary>Original request body; query-string continuations must resend it.</summary>
    public JsonObject RequireBody() =>
        Body ?? throw new ArgumentException($"The '{Scope}' continuation token carries no request body.");
}

/// <summary>
/// Opaque continuation tokens handed to Core callers. Format:
/// base64url(UTF-8 JSON <c>{"s": scope, "t": raw InnerTube token, "b": original request body?, "f": search filter?}</c>).
/// Query-string continuations (home, library, filtered search, watch) must resend the first request's
/// body with <c>&amp;ctoken=</c>; body-style continuations (playlist tracks) only need the token.
/// </summary>
internal static class ContinuationToken
{
    /// <summary>Returns null when <paramref name="rawToken"/> is null or empty (no more pages).</summary>
    public static string? Wrap(string scope, string? rawToken, JsonObject? body = null, string? filter = null)
    {
        if (string.IsNullOrEmpty(rawToken))
        {
            return null;
        }

        var state = new JsonObject { ["s"] = scope, ["t"] = rawToken };
        if (body is not null)
        {
            state["b"] = body.DeepClone();
        }

        if (filter is not null)
        {
            state["f"] = filter;
        }

        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(state.ToJsonString()));
    }

    /// <summary>Throws <see cref="ArgumentException"/> for malformed tokens or tokens from another method.</summary>
    public static ContinuationState Unwrap(string token, string expectedScope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        JsonObject state;
        try
        {
            state = JsonNode.Parse(Base64Url.DecodeFromChars(token)) as JsonObject
                ?? throw new ArgumentException("Invalid continuation token.", nameof(token));
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new ArgumentException("Invalid continuation token.", nameof(token), ex);
        }

        var scope = ReadString(state, "s");
        var raw = ReadString(state, "t");
        if (scope is null || string.IsNullOrEmpty(raw))
        {
            throw new ArgumentException("Invalid continuation token.", nameof(token));
        }

        if (scope != expectedScope)
        {
            throw new ArgumentException($"This continuation token belongs to '{scope}', not '{expectedScope}'.", nameof(token));
        }

        var body = state.TryGetPropertyValue("b", out var b) ? b as JsonObject : null;
        return new ContinuationState(scope, raw, body, ReadString(state, "f"));
    }

    private static string? ReadString(JsonObject state, string key) =>
        state.TryGetPropertyValue(key, out var value) && value is JsonValue v && v.GetValueKind() == JsonValueKind.String
            ? v.GetValue<string>()
            : null;
}
