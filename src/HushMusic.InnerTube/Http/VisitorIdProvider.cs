using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.InnerTube.Http;

/// <summary>
/// Supplies the <c>X-Goog-Visitor-Id</c> header value. Fetched like ytmusicapi's
/// <c>helpers.get_visitor_id</c> (anonymous GET of the music.youtube.com page, first <c>ytcfg.set({...});</c>,
/// <c>VISITOR_DATA</c>), then cached in memory and in <c>cache/visitor_id.txt</c>. Not a credential.
/// </summary>
internal sealed partial class VisitorIdProvider(
    IHttpClientFactory httpClientFactory,
    IAppPaths paths,
    TimeProvider timeProvider,
    ILogger<VisitorIdProvider> logger)
{
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _visitorId;
    private DateTimeOffset _nextFetchAttempt = DateTimeOffset.MinValue;

    private string FilePath => Path.Combine(paths.Cache, "visitor_id.txt");

    /// <summary>Returns the visitor id, or an empty string (as ytmusicapi sends) when none is available.</summary>
    public async Task<string> GetAsync(CancellationToken cancellationToken)
    {
        if (_visitorId is { } cached)
        {
            return cached;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_visitorId is { } loaded)
            {
                return loaded;
            }

            var fromFile = await TryReadFileAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(fromFile))
            {
                _visitorId = fromFile;
                return fromFile;
            }

            if (timeProvider.GetUtcNow() < _nextFetchAttempt)
            {
                return string.Empty;
            }

            var fetched = await TryFetchAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(fetched))
            {
                _nextFetchAttempt = timeProvider.GetUtcNow() + FailureCooldown;
                return string.Empty;
            }

            _visitorId = fetched;
            await TryWriteFileAsync(fetched, cancellationToken).ConfigureAwait(false);
            return fetched;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string? ExtractVisitorId(string html)
    {
        var match = YtcfgRegex().Match(html);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            return JsonLookup.GetString(JsonNode.Parse(match.Groups[1].Value), "VISITOR_DATA");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> TryFetchAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(InnerTubeClient.HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, InnerTubeConstants.Domain);

            // ytmusicapi uses its base headers here (no auth), even in cookie mode.
            request.Headers.TryAddWithoutValidation("User-Agent", InnerTubeConstants.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", "*/*");
            request.Headers.TryAddWithoutValidation("Origin", InnerTubeConstants.Domain);
            request.Headers.TryAddWithoutValidation("Cookie", InnerTubeConstants.ConsentCookie);

            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogFetchFailed(logger, $"HTTP {(int)response.StatusCode}");
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var visitorId = ExtractVisitorId(html);
            if (string.IsNullOrEmpty(visitorId))
            {
                LogFetchFailed(logger, "no VISITOR_DATA in the first ytcfg.set block");
            }

            return visitorId;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
        {
            LogFetchFailed(logger, ex.Message);
            return null;
        }
    }

    private async Task<string?> TryReadFileAsync(CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(FilePath)
                ? (await File.ReadAllTextAsync(FilePath, cancellationToken).ConfigureAwait(false)).Trim()
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileError(logger, ex);
            return null;
        }
    }

    private async Task TryWriteFileAsync(string visitorId, CancellationToken cancellationToken)
    {
        try
        {
            await File.WriteAllTextAsync(FilePath, visitorId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogFileError(logger, ex);
        }
    }

    [GeneratedRegex(@"ytcfg\.set\s*\(\s*({.+?})\s*\)\s*;")]
    private static partial Regex YtcfgRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch the InnerTube visitor id: {Reason}")]
    private static partial void LogFetchFailed(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read or write the cached visitor id")]
    private static partial void LogFileError(ILogger logger, Exception exception);
}
