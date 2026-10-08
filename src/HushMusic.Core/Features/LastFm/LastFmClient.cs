using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HushMusic.Core.Features.LastFm;

/// <summary>
/// The Last.fm 2.0 web service calls this app uses. Every call is a signed form POST with <c>format=json</c>.
/// API errors surface as <see cref="LastFmException"/>; network failures as <see cref="HttpRequestException"/>.
/// </summary>
internal sealed class LastFmClient(IHttpClientFactory httpClientFactory)
{
    public const string HttpClientName = "LastFm";

    public static readonly Uri ApiRoot = new("https://ws.audioscrobbler.com/2.0/");

    public static readonly ProductInfoHeaderValue UserAgent = new("HushMusic", "1.0");

    /// <summary>Approval page for a desktop-auth token.</summary>
    public static Uri AuthorizationUri(string apiKey, string token) =>
        new($"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(apiKey)}&token={Uri.EscapeDataString(token)}");

    /// <summary>auth.getToken: an unauthorized request token, valid for 60 minutes.</summary>
    public async Task<string> GetTokenAsync(LastFmCredentials credentials, CancellationToken cancellationToken)
    {
        var json = await CallAsync("auth.getToken", credentials, [], cancellationToken).ConfigureAwait(false);
        return Text(json["token"]) ?? throw Malformed("auth.getToken");
    }

    /// <summary>auth.getSession: exchanges an approved token for a session key that never expires.</summary>
    public async Task<LastFmSession> GetSessionAsync(LastFmCredentials credentials, string token, CancellationToken cancellationToken)
    {
        var json = await CallAsync("auth.getSession", credentials, [new("token", token)], cancellationToken).ConfigureAwait(false);
        var session = json["session"] as JsonObject;
        var name = Text(session?["name"]);
        var key = Text(session?["key"]);
        return name is null || key is null ? throw Malformed("auth.getSession") : new LastFmSession(name, key);
    }

    public async Task UpdateNowPlayingAsync(
        LastFmCredentials credentials,
        string sessionKey,
        LastFmScrobble track,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> parameters = [new("sk", sessionKey), new("artist", track.Artist), new("track", track.Track)];
        if (track.Album is { } album)
        {
            parameters.Add(new("album", album));
        }

        if (track.DurationSeconds is { } duration)
        {
            parameters.Add(new("duration", duration.ToString(CultureInfo.InvariantCulture)));
        }

        await CallAsync("track.updateNowPlaying", credentials, parameters, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>track.scrobble with up to 50 entries, using the indexed array parameters (artist[i], track[i], …).</summary>
    public async Task<LastFmScrobbleResult> ScrobbleAsync(
        LastFmCredentials credentials,
        string sessionKey,
        IReadOnlyList<LastFmScrobble> batch,
        CancellationToken cancellationToken)
    {
        if (batch.Count is 0 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(batch), batch.Count, "A scrobble batch holds 1 to 50 entries.");
        }

        List<KeyValuePair<string, string>> parameters = [new("sk", sessionKey)];
        for (var i = 0; i < batch.Count; i++)
        {
            var scrobble = batch[i];
            parameters.Add(new($"artist[{i}]", scrobble.Artist));
            parameters.Add(new($"track[{i}]", scrobble.Track));
            parameters.Add(new($"timestamp[{i}]", scrobble.Timestamp.ToString(CultureInfo.InvariantCulture)));
            if (scrobble.Album is { } album)
            {
                parameters.Add(new($"album[{i}]", album));
            }

            if (scrobble.DurationSeconds is { } duration)
            {
                parameters.Add(new($"duration[{i}]", duration.ToString(CultureInfo.InvariantCulture)));
            }
        }

        var json = await CallAsync("track.scrobble", credentials, parameters, cancellationToken).ConfigureAwait(false);
        var attributes = (json["scrobbles"] as JsonObject)?["@attr"] as JsonObject;
        return new LastFmScrobbleResult(Number(attributes?["accepted"]) ?? batch.Count, Number(attributes?["ignored"]) ?? 0);
    }

    private async Task<JsonObject> CallAsync(
        string method,
        LastFmCredentials credentials,
        IEnumerable<KeyValuePair<string, string>> extra,
        CancellationToken cancellationToken)
    {
        List<KeyValuePair<string, string>> parameters = [new("method", method), new("api_key", credentials.ApiKey), .. extra];
        parameters.Add(new("api_sig", LastFmSignature.Sign(parameters, credentials.SharedSecret)));
        parameters.Add(new("format", "json"));

        var client = httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoot) { Content = new FormUrlEncodedContent(parameters) };
        request.Headers.UserAgent.Add(UserAgent);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        JsonObject? json = null;
        try
        {
            json = JsonNode.Parse(body) as JsonObject;
        }
        catch (JsonException)
        {
        }

        // Errors come back as {"error": code, "message": "..."}, with HTTP 200 or a 4xx status depending on the method.
        if (Number(json?["error"]) is { } code)
        {
            throw new LastFmException(code, Text(json?["message"]) ?? $"Last.fm error {code}.");
        }

        if (!response.IsSuccessStatusCode || json is null)
        {
            throw new HttpRequestException($"Last.fm returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return json;
    }

    private static LastFmException Malformed(string method) =>
        new(LastFmException.OperationFailed, $"Last.fm sent an unexpected response to {method}.");

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    // Last.fm sends numbers either as JSON numbers or as strings ("accepted": "1").
    private static int? Number(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)
            ? number
            : null;
    }
}
