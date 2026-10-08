using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HushMusic.Core;
using HushMusic.Core.Abstractions;

namespace HushMusic.Playback.YtDlp;

/// <summary>Parses yt-dlp's <c>-j</c> output and error text.</summary>
internal static partial class YtDlpOutput
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    public static ResolvedStream ParseStream(string videoId, string stdout, DateTimeOffset now)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'))
            ?? throw new StreamResolutionException(videoId, "yt-dlp returned no stream information.");

        JsonObject root;
        try
        {
            root = JsonNode.Parse(line) as JsonObject
                ?? throw new StreamResolutionException(videoId, "yt-dlp returned unexpected output.");
        }
        catch (JsonException ex)
        {
            throw new StreamResolutionException(videoId, "yt-dlp returned unreadable output.", ex);
        }

        // The selected format is merged into the top level. Only a video+audio merge lacks a top-level url.
        var format = root;
        if (Text(root["url"]) is null && root["requested_formats"] is JsonArray requested)
        {
            format = requested.OfType<JsonObject>().FirstOrDefault(f => Text(f["vcodec"]) == "none")
                ?? requested.OfType<JsonObject>().FirstOrDefault()
                ?? root;
        }

        var url = Text(format["url"]);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new StreamResolutionException(videoId, "yt-dlp did not return a playable URL.");
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((format["http_headers"] ?? root["http_headers"]) is JsonObject headerObject)
        {
            foreach (var (name, value) in headerObject)
            {
                if (Text(value) is { } text)
                {
                    headers[name] = text;
                }
            }
        }

        var bitrate = Number(format["abr"]) ?? Number(format["tbr"]);
        var duration = Number(root["duration"]);
        return new ResolvedStream
        {
            VideoId = videoId,
            Url = uri,
            Container = Text(format["ext"]),
            Codec = Text(format["acodec"]) is { } codec && codec != "none" ? codec : null,
            BitrateKbps = bitrate is > 0 ? (int)Math.Round(bitrate.Value) : null,
            Duration = duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null,
            ExpiresAt = ParseExpiry(uri) ?? now + DefaultLifetime,
            Headers = headers,
        };
    }

    /// <summary>googlevideo URLs carry their expiry as unix seconds in <c>expire=</c> (query) or <c>/expire/</c> (path).</summary>
    public static DateTimeOffset? ParseExpiry(Uri url)
    {
        var match = ExpireRegex().Match(url.PathAndQuery);
        return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
    }

    /// <summary>The last "ERROR:" line from yt-dlp's stderr, without the "ERROR: [youtube] &lt;id&gt;:" prefix.</summary>
    public static string? FindError(string stderr)
    {
        var line = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var message = ErrorPrefixRegex().Replace(line, string.Empty).Trim();
        return message.Length > 0 ? message : line;
    }

    /// <summary>
    /// The first warning or error in which yt-dlp says it could not solve YouTube's JavaScript challenges ("No supported
    /// JavaScript runtime could be found", "n challenge solving failed", "[jsc:deno] …"), or null.
    /// </summary>
    public static string? FindJsRuntimeProblem(string stderr) =>
        stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => (l.StartsWith("WARNING:", StringComparison.Ordinal) || l.StartsWith("ERROR:", StringComparison.Ordinal))
                && JsRuntimeProblemRegex().IsMatch(l));

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;

    [GeneratedRegex(@"[?&/]expire[=/](\d+)")]
    private static partial Regex ExpireRegex();

    [GeneratedRegex(@"^ERROR:\s*(\[[^\]]+\]\s*)?([A-Za-z0-9_-]{11}:\s*)?")]
    private static partial Regex ErrorPrefixRegex();

    [GeneratedRegex(@"JavaScript runtime|challenge solving failed|Signature solving failed|\[jsc[:\]]", RegexOptions.IgnoreCase)]
    private static partial Regex JsRuntimeProblemRegex();
}
