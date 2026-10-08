using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// <see cref="IRadioDirectory"/> on the Radio Browser API (api.radio-browser.info). The API runs on a few mirrors listed
/// at all.api.radio-browser.info/json/servers; a failing mirror is skipped for the next one. Requests carry a descriptive
/// User-Agent, as the API asks. Results are cached in memory for <see cref="CacheTime"/>.
/// </summary>
public sealed class RadioBrowserClient(IHttpClientFactory httpClientFactory, TimeProvider time, ILogger<RadioBrowserClient> logger)
    : IRadioDirectory
{
    public const string HttpClientName = "RadioBrowser";

    public static readonly ProductInfoHeaderValue UserAgent = new("HushMusic", "1.0");

    public static readonly TimeSpan CacheTime = TimeSpan.FromMinutes(15);

    /// <summary>Used when the server list can't be fetched (checked 2026-10-08: de1 and de2 answer; nl1/at1 no longer resolve).</summary>
    public static readonly IReadOnlyList<string> FallbackServers = ["de1.api.radio-browser.info", "de2.api.radio-browser.info", "all.api.radio-browser.info"];

    private static readonly Uri ServerListUri = new("https://all.api.radio-browser.info/json/servers");

    // The first request doesn't wait longer than this for the mirror list; the built-in list serves it meanwhile.
    private static readonly TimeSpan DiscoveryWait = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(5);
    private const int MaxCacheEntries = 64;
    private const int MaxAttempts = 3;

    private readonly ConcurrentDictionary<string, (DateTimeOffset At, IReadOnlyList<RadioStation> Stations)> _cache = new(StringComparer.Ordinal);
    private readonly Lock _serverGate = new();
    private Task<IReadOnlyList<string>>? _servers;
    private int _serverIndex;

    public async Task<IReadOnlyList<RadioStation>> GetByTagsAsync(IReadOnlyList<string> tags, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tags);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        var lists = await Task.WhenAll(tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(tag =>
            GetStationsAsync($"json/stations/search?tag={Uri.EscapeDataString(tag.Trim())}&tagExact=true&order=clickcount&reverse=true&hidebroken=true&limit={Fetch(limit)}", cancellationToken)))
            .ConfigureAwait(false);
        return RadioBrowserParser.Interleave(lists, limit);
    }

    public async Task<IReadOnlyList<RadioStation>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var stations = await GetStationsAsync(
            $"json/stations/search?name={Uri.EscapeDataString(query.Trim())}&order=clickcount&reverse=true&hidebroken=true&limit={Fetch(limit)}",
            cancellationToken).ConfigureAwait(false);
        return stations.Count > limit ? [.. stations.Take(limit)] : stations;
    }

    public async Task CountClickAsync(string stationId, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(stationId, out var uuid))
        {
            return; // curated station that is not in the directory
        }

        try
        {
            using var _ = await SendAsync($"json/url/{uuid:D}", cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Counted a play of station {StationId} on Radio Browser", stationId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not count a play of station {StationId}", stationId);
        }
    }

    // Some results are filtered out (codecs, HLS, duplicates): ask for more than needed.
    private static int Fetch(int limit) => Math.Min(limit * 2, 500);

    private async Task<IReadOnlyList<RadioStation>> GetStationsAsync(string path, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (_cache.TryGetValue(path, out var cached) && now - cached.At < CacheTime)
        {
            return cached.Stations;
        }

        using var response = await SendAsync(path, cancellationToken).ConfigureAwait(false);
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var stations = RadioBrowserParser.ParseStations(json);
        if (_cache.Count >= MaxCacheEntries)
        {
            _cache.Clear();
        }

        _cache[path] = (now, stations);
        return stations;
    }

    /// <summary>GETs <paramref name="path"/> from a working mirror, moving on to the next one after a network or server error.</summary>
    private async Task<HttpResponseMessage> SendAsync(string path, CancellationToken cancellationToken)
    {
        var servers = await GetServersAsync(cancellationToken).ConfigureAwait(false);
        var client = httpClientFactory.CreateClient(HttpClientName);
        Exception? last = null;
        for (var attempt = 0; attempt < Math.Min(MaxAttempts, servers.Count); attempt++)
        {
            var index = Volatile.Read(ref _serverIndex) % servers.Count;
            var uri = new Uri($"https://{servers[index]}/{path}");
            try
            {
                var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if ((int)response.StatusCode < 500)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        var status = (int)response.StatusCode;
                        response.Dispose();
                        throw new HushException($"The radio directory refused the request (HTTP {status.ToString(CultureInfo.InvariantCulture)}).");
                    }

                    return response;
                }

                last = new HttpRequestException($"HTTP {(int)response.StatusCode} from {uri.Host}");
                response.Dispose();
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                last = ex;
            }

            logger.LogDebug(last, "Radio Browser mirror {Server} failed; trying the next one", servers[index]);
            Interlocked.CompareExchange(ref _serverIndex, index + 1, index);
        }

        throw new HushException("Couldn't reach the radio directory. Check your connection and try again.", last);
    }

    private async Task<IReadOnlyList<string>> GetServersAsync(CancellationToken cancellationToken)
    {
        Task<IReadOnlyList<string>> discovery;
        lock (_serverGate)
        {
            discovery = _servers ??= DiscoverServersAsync();
        }

        if (!discovery.IsCompleted)
        {
            await Task.WhenAny(discovery, Task.Delay(DiscoveryWait, cancellationToken)).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }

        return discovery.IsCompletedSuccessfully ? discovery.Result : FallbackServers;
    }

    private async Task<IReadOnlyList<string>> DiscoverServersAsync()
    {
        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var timeout = new CancellationTokenSource(DiscoveryTimeout);
            var json = await client.GetStringAsync(ServerListUri, timeout.Token).ConfigureAwait(false);
            var names = (JsonNode.Parse(json) as JsonArray ?? new JsonArray())
                .Select(n => n?["name"]?.GetValue<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n) && n.EndsWith(".api.radio-browser.info", StringComparison.OrdinalIgnoreCase))
                .Select(n => n!.ToLowerInvariant())
                .Distinct()
                .OrderBy(_ => Random.Shared.Next())
                .ToList();
            if (names.Count > 0)
            {
                logger.LogDebug("Radio Browser mirrors: {Servers}", string.Join(", ", names));
                return [.. names, .. FallbackServers.Where(f => !names.Contains(f))];
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException)
        {
            logger.LogDebug(ex, "Could not list the Radio Browser mirrors; using the built-in list");
        }

        return FallbackServers;
    }
}
