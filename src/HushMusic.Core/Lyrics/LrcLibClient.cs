using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace HushMusic.Core.Lyrics;

/// <summary>
/// LRCLIB (lrclib.net), a free, open lyrics database with no key: <c>/api/get</c> for an exact match on title, artist,
/// album and length (LRCLIB allows ±2 s), <c>/api/search</c> when that finds nothing usable. Requests carry the
/// User-Agent LRCLIB asks clients for (name, version, homepage).
/// </summary>
public sealed partial class LrcLibClient(IHttpClientFactory httpClientFactory, ILogger<LrcLibClient> logger)
{
    public const string HttpClientName = "LRCLIB";

    public static readonly Uri ApiRoot = new("https://lrclib.net/api/");

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(0.5);

    /// <summary>"Hush/0.5.2 (https://github.com/qblaize/HushMusic)".</summary>
    public static IReadOnlyList<ProductInfoHeaderValue> UserAgent { get; } =
    [
        new("Hush", AppVersion()),
        new("(https://github.com/qblaize/HushMusic)"),
    ];

    /// <summary>The named HttpClient's settings (registered in <c>AddHushCore</c>).</summary>
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = Timeout;
        foreach (var product in UserAgent)
        {
            client.DefaultRequestHeaders.UserAgent.Add(product);
        }
    }

    /// <summary>
    /// The record for this track, or null when LRCLIB has none. Tries <c>/api/get</c> first; when it finds nothing, a
    /// placeholder, or plain lyrics only, <c>/api/search</c> is asked for a better match of the same song
    /// (<see cref="LrcLib.PickBest"/>).
    /// </summary>
    public async Task<LrcLibRecord?> FindAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var exact = await GetAsync(query, cancellationToken).ConfigureAwait(false);
        var exactRank = exact is null ? -1 : LrcLib.LyricsRank(exact);
        if (exactRank >= 2 || (exactRank == 0 && exact is not null))
        {
            return exact;
        }

        var found = await SearchAsync(LyricsText.CoreTitle(query.Title), query.Artist, cancellationToken).ConfigureAwait(false);
        var best = LrcLib.PickBest(found, query);
        if (best is not null && LrcLib.LyricsRank(best) > exactRank)
        {
            LogPicked(logger, best.Id, query.Title, query.Artist);
            return best;
        }

        return exactRank >= 0 ? exact : null;
    }

    /// <summary><c>/api/get</c>: null on 404 (no match).</summary>
    public async Task<LrcLibRecord?> GetAsync(LyricsQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var path = new StringBuilder("get?track_name=").Append(Uri.EscapeDataString(query.Title))
            .Append("&artist_name=").Append(Uri.EscapeDataString(query.Artist));
        if (query.Album is { Length: > 0 } album)
        {
            path.Append("&album_name=").Append(Uri.EscapeDataString(album));
        }

        if (query.Duration is { } duration)
        {
            path.Append("&duration=").Append(Math.Round(duration.TotalSeconds).ToString(CultureInfo.InvariantCulture));
        }

        var json = await SendAsync(path.ToString(), cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        try
        {
            return LrcLib.ParseRecord(JsonNode.Parse(json));
        }
        catch (JsonException ex)
        {
            throw new HushException("LRCLIB returned a response that isn't valid JSON.", ex);
        }
    }

    /// <summary><c>/api/search</c> by title and artist.</summary>
    public async Task<IReadOnlyList<LrcLibRecord>> SearchAsync(string title, string artist, CancellationToken cancellationToken = default)
    {
        var path = $"search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(artist)}";
        var json = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        return json is null ? [] : LrcLib.ParseSearch(json);
    }

    /// <summary>
    /// The response body, or null on 404. A server error (LRCLIB answers 503 while it is busy) is retried once after
    /// <see cref="RetryDelay"/>. Other failures throw <see cref="HushException"/>.
    /// </summary>
    private async Task<string?> SendAsync(string path, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var uri = new Uri(ApiRoot, path);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                using var response = await client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if ((int)response.StatusCode >= 500 && attempt == 1)
                {
                    LogRetry(logger, (int)response.StatusCode);
                    await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (!response.IsSuccessStatusCode)
                {
                    throw new HushException($"LRCLIB returned HTTP {(int)response.StatusCode}.");
                }

                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (HttpRequestException ex)
        {
            throw new HushException("Couldn't reach LRCLIB.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HushException("LRCLIB didn't answer in time.", ex);
        }
    }

    private static string AppVersion()
    {
        var version = typeof(LrcLibClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var plus = version?.IndexOf('+', StringComparison.Ordinal) ?? -1;
        version = plus > 0 ? version![..plus] : version;
        return string.IsNullOrWhiteSpace(version) ? "1.0" : version;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "LRCLIB returned HTTP {StatusCode}; asking once more")]
    private static partial void LogRetry(ILogger logger, int statusCode);

    [LoggerMessage(Level = LogLevel.Debug, Message = "LRCLIB search picked record {Id} for \"{Title}\" by {Artist}")]
    private static partial void LogPicked(ILogger logger, long id, string title, string artist);
}
