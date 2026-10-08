using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using HushMusic.Core;
using HushMusic.Core.Abstractions;

namespace HushMusic.InnerTube.Http;

/// <summary>
/// The single InnerTube transport, modelled on ytmusicapi's <c>YTMusicBase._send_request</c>:
/// POST <c>youtubei/v1/{endpoint}?alt=json[&amp;key=…]&amp;prettyPrint=false</c> with the body plus <c>context</c>.
/// </summary>
internal sealed partial class InnerTubeClient(
    IHttpClientFactory httpClientFactory,
    IRequestAuthenticator authenticator,
    ISettingsService settings,
    VisitorIdProvider visitorIds,
    IOptions<InnerTubeOptions> options,
    TimeProvider timeProvider,
    ILogger<InnerTubeClient> logger) : IInnerTubeClient
{
    public const string HttpClientName = "HushMusic.InnerTube";

    private const string TrackingLabel = "api/stats/playback";

    private readonly InnerTubeOptions _options = options.Value;

    public async Task<JsonNode> PostAsync(InnerTubeRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var withAccount = !request.Anonymous;
        var authenticated = withAccount && authenticator.IsAuthenticated;
        if (request.RequiresAuth && !authenticated)
        {
            throw new AuthRequiredException();
        }

        var uri = BuildUri(request, authenticated);
        var payload = Encoding.UTF8.GetBytes(BuildPayload(request).ToJsonString());
        var visitorId = await visitorIds.GetAsync(cancellationToken).ConfigureAwait(false);

        using var response = await SendWithRetryAsync(
            request.Endpoint,
            request.AllowRetry,
            withAccount,
            () => CreateMessageAsync(HttpMethod.Post, uri, payload, visitorId, withAccount, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        return await ReadJsonAsync(request, response, authenticated, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendTrackingPingAsync(Uri url, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(url);

        // The ping carries the account cookies, so never send it anywhere but YouTube.
        if (url.Scheme != Uri.UriSchemeHttps
            || !(url.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                 || url.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InnerTubeException(TrackingLabel, $"Refusing to send a tracking request to unexpected host '{url.Host}'.");
        }

        var authenticated = authenticator.IsAuthenticated;
        var visitorId = await visitorIds.GetAsync(cancellationToken).ConfigureAwait(false);

        using var response = await SendWithRetryAsync(
            TrackingLabel,
            allowRetry: false,
            withAccount: true,
            () => CreateMessageAsync(HttpMethod.Get, url, null, visitorId, withAccount: true, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (SessionRejection.IsSignedOutError(status, null))
        {
            throw SessionRejected(authenticated, TrackingLabel, $"HTTP {status}");
        }

        throw new InnerTubeException(TrackingLabel, $"YouTube returned HTTP {status} ({response.ReasonPhrase}) for the playback ping.", status);
    }

    private Uri BuildUri(InnerTubeRequest request, bool authenticated)
    {
        var url = new StringBuilder(InnerTubeConstants.BaseApi)
            .Append(request.Endpoint)
            .Append("?alt=json");

        // ytmusicapi adds the public key only for browser (cookie) auth.
        if (authenticated && authenticator.Mode == AuthMode.Cookies)
        {
            url.Append("&key=").Append(_options.ApiKey);
        }

        url.Append("&prettyPrint=false");

        // Legacy continuations repeat the token twice and append it raw: it is already percent-encoded.
        if (request.QueryContinuation is { } token)
        {
            url.Append("&ctoken=").Append(token).Append("&continuation=").Append(token);
        }

        return new Uri(url.ToString());
    }

    private JsonObject BuildPayload(InnerTubeRequest request)
    {
        var payload = (JsonObject)request.Body.DeepClone();
        payload["context"] = ClientContext.Create(timeProvider.GetUtcNow(), settings.Current.ContentLocation, request.Client);
        return payload;
    }

    private static ByteArrayContent CreateJsonContent(byte[] payload)
    {
        var content = new ByteArrayContent(payload);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        // ytmusicapi declares "content-encoding: gzip" but sends the body uncompressed; the server accepts it.
        content.Headers.ContentEncoding.Add("gzip");
        return content;
    }

    /// <summary>A fresh message per attempt: content cannot be resent and SAPISIDHASH carries a timestamp.</summary>
    private async Task<HttpRequestMessage> CreateMessageAsync(HttpMethod method, Uri uri, byte[]? payload, string visitorId, bool withAccount, CancellationToken cancellationToken)
    {
        var message = new HttpRequestMessage(method, uri);
        try
        {
            if (payload is not null)
            {
                message.Content = CreateJsonContent(payload);
            }

            await PrepareHeadersAsync(message, visitorId, withAccount, cancellationToken).ConfigureAwait(false);
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    private async Task PrepareHeadersAsync(HttpRequestMessage message, string visitorId, bool withAccount, CancellationToken cancellationToken)
    {
        var headers = message.Headers;
        headers.TryAddWithoutValidation("User-Agent", InnerTubeConstants.UserAgent);
        headers.TryAddWithoutValidation("Accept", "*/*");
        headers.TryAddWithoutValidation("Origin", InnerTubeConstants.Domain);
        headers.TryAddWithoutValidation("X-Goog-Visitor-Id", visitorId);

        // Adds Cookie, SAPISIDHASH Authorization, X-Goog-AuthUser and X-Origin when signed in.
        // Needs the absolute RequestUri: it only ever sends credentials to https://*.youtube.com.
        if (withAccount)
        {
            await authenticator.ApplyAsync(message, cancellationToken).ConfigureAwait(false);
        }

        // Like Python requests: an explicit Cookie header (cookie mode) replaces the SOCS consent cookie.
        if (!headers.Contains("Cookie"))
        {
            headers.TryAddWithoutValidation("Cookie", InnerTubeConstants.ConsentCookie);
        }
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        string label,
        bool allowRetry,
        bool withAccount,
        Func<Task<HttpRequestMessage>> createMessage,
        CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);

        for (var attempt = 1; ; attempt++)
        {
            var canRetry = allowRetry && attempt == 1;
            var started = timeProvider.GetTimestamp();
            HttpResponseMessage response;

            using (var message = await createMessage().ConfigureAwait(false))
            {
                try
                {
                    response = await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    LogTransportFailure(logger, label, attempt, ex.Message);
                    if (canRetry)
                    {
                        await Task.Delay(_options.RetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new InnerTubeException(label, $"Could not reach YouTube Music: {ex.Message}", null, ex);
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    // HttpClient.Timeout elapsed (the caller did not cancel).
                    LogTransportFailure(logger, label, attempt, "timeout");
                    if (canRetry)
                    {
                        await Task.Delay(_options.RetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new InnerTubeException(label, $"YouTube Music did not respond within {client.Timeout.TotalSeconds:0} seconds.", null, ex);
                }
            }

            var status = (int)response.StatusCode;
            LogResponse(logger, label, status, timeProvider.GetElapsedTime(started).TotalMilliseconds);

            // An anonymous response's Set-Cookie belongs to a visitor session, not the account's.
            if (withAccount)
            {
                await NotifyAuthenticatorAsync(response, cancellationToken).ConfigureAwait(false);
            }

            if (canRetry && status >= 500)
            {
                response.Dispose();
                await Task.Delay(_options.RetryDelay, timeProvider, cancellationToken).ConfigureAwait(false);
                continue;
            }

            return response;
        }
    }

    private async Task NotifyAuthenticatorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await authenticator.OnResponseAsync(response, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cookie rotation is best effort; it must not fail the data request.
            LogAuthenticatorFailure(logger, ex);
        }
    }

    private async Task<JsonNode> ReadJsonAsync(InnerTubeRequest request, HttpResponseMessage response, bool authenticated, CancellationToken cancellationToken)
    {
        var status = (int)response.StatusCode;
        JsonNode? json = null;
        JsonException? parseError = null;

        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                json = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (JsonException ex)
        {
            parseError = ex;
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorMessage = JsonLookup.GetString(json, "error", "message");
            if (SessionRejection.IsSignedOutError(status, errorMessage))
            {
                throw SessionRejected(authenticated, request.Endpoint, $"HTTP {status}: {errorMessage ?? response.ReasonPhrase}");
            }

            var detail = string.IsNullOrWhiteSpace(errorMessage) ? string.Empty : " " + errorMessage;
            throw new InnerTubeException(
                request.Endpoint,
                $"YouTube Music returned HTTP {status} ({response.ReasonPhrase}) for '{request.Endpoint}'.{detail}",
                status);
        }

        if (json is null)
        {
            throw new InnerTubeException(request.Endpoint, $"YouTube Music returned an invalid response for '{request.Endpoint}'.", status, parseError);
        }

        if (authenticated)
        {
            var reason = request.SignedOutCheck?.Invoke(json) ?? SessionRejection.FindSignInPrompt(json);
            if (reason is not null)
            {
                throw SessionRejected(authenticated, request.Endpoint, reason);
            }
        }

        return json;
    }

    private AuthRequiredException SessionRejected(bool authenticated, string endpoint, string reason)
    {
        if (!authenticated)
        {
            return new AuthRequiredException();
        }

        LogSessionRejected(logger, endpoint, reason);
        authenticator.ReportSessionRejected($"{endpoint}: {reason}");
        return new AuthRequiredException(SessionRejection.ExpiredMessage);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "InnerTube {Endpoint} -> HTTP {StatusCode} in {ElapsedMs:0} ms")]
    private static partial void LogResponse(ILogger logger, string endpoint, int statusCode, double elapsedMs);

    [LoggerMessage(Level = LogLevel.Warning, Message = "InnerTube {Endpoint} attempt {Attempt} failed: {Reason}")]
    private static partial void LogTransportFailure(ILogger logger, string endpoint, int attempt, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "YouTube Music rejected the signed-in session on {Endpoint}: {Reason}")]
    private static partial void LogSessionRejected(ILogger logger, string endpoint, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The request authenticator failed to process a response")]
    private static partial void LogAuthenticatorFailure(ILogger logger, Exception exception);
}
