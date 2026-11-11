using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Radio;

/// <summary>
/// <see cref="IStationProbe"/>: connects to the link, follows a .pls / .m3u / .asx / .xspf playlist to the stream it
/// names, checks that the server answers with audio rather than a web page, and reads the station's ICY headers (name,
/// genre, bitrate, website). Only the headers (or a playlist's text) are read, then the connection closes.
/// </summary>
public sealed class StationProbe(IHttpClientFactory httpClients, ILogger<StationProbe> logger) : IStationProbe
{
    public const string HttpClientName = "StationProbe";

    private const int MaxPlaylistBytes = 64 * 1024;
    private const int MaxPlaylistHops = 3;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);

    public async Task<StationProbeResult> ProbeAsync(Uri link, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(link);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        var client = httpClients.CreateClient(HttpClientName);
        var url = link;
        try
        {
            for (var hop = 0; ; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
                HttpResponseMessage response;
                try
                {
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex) when (ex.HttpRequestError == HttpRequestError.InvalidResponse)
                {
                    // Old SHOUTcast servers answer "ICY 200 OK", which Windows plays but HttpClient can't read.
                    logger.LogDebug("{Host} answered with a non-HTTP status line; taking it as an ICY stream", url.Host);
                    return new StationProbeResult(url, null, null, null, null, null);
                }

                using (response)
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HushException($"The server answered {(int)response.StatusCode} {response.ReasonPhrase}.".TrimEnd() + " Check the link.");
                    }

                    var finalUrl = response.RequestMessage?.RequestUri ?? url;
                    var mediaType = response.Content.Headers.ContentType?.MediaType;
                    switch (CustomStations.Classify(mediaType, finalUrl))
                    {
                        case StreamLinkKind.WebPage:
                            throw new HushException("That link opens a web page, not an audio stream. Look for a direct stream or a .pls / .m3u link on the station's website.");

                        case StreamLinkKind.Playlist:
                            var text = await ReadTextAsync(response, timeout.Token).ConfigureAwait(false);
                            if (CustomStations.IsHls(text))
                            {
                                return new StationProbeResult(finalUrl, null, null, null, null, null);
                            }

                            var next = CustomStations.FirstStreamIn(text, finalUrl)
                                ?? throw new HushException("That playlist doesn't name a stream.");
                            if (hop + 1 >= MaxPlaylistHops)
                            {
                                throw new HushException("That link leads from playlist to playlist without reaching a stream.");
                            }

                            url = next;
                            continue;

                        default:
                            return new StationProbeResult(
                                finalUrl,
                                Header(response, "icy-name"),
                                Header(response, "icy-genre"),
                                CustomStations.CodecFor(mediaType),
                                CustomStations.ParseBitrate(Header(response, "icy-br")),
                                Header(response, "icy-url"));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new HushException("The station didn't answer in time.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogDebug(ex, "Checking the stream link {Host} failed", url.Host);
            throw new HushException("Couldn't connect to that link. Check it and your connection.", ex);
        }
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        (response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values))
            ? values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim()
            : null;

    // A playlist is small; a mislabelled live stream never ends, so the read stops at a size limit.
    private static async Task<string> ReadTextAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            var buffer = new byte[MaxPlaylistBytes];
            var length = 0;
            while (length < buffer.Length)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length += read;
            }

            var charset = response.Content.Headers.ContentType?.CharSet;
            var encoding = Encoding.UTF8;
            try
            {
                if (!string.IsNullOrWhiteSpace(charset))
                {
                    encoding = Encoding.GetEncoding(charset.Trim('"'));
                }
            }
            catch (ArgumentException)
            {
            }

            return encoding.GetString(buffer, 0, length);
        }
    }

    /// <summary>The probe's HTTP client: no overall timeout (each probe has its own), the radio user agent.</summary>
    public static void Configure(HttpClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        client.Timeout = System.Threading.Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.UserAgent.Add(RadioBrowserClient.UserAgent);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
    }
}
