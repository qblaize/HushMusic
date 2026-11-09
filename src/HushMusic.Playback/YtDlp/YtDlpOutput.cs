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

    public static ResolvedStream ParseStream(string videoId, string stdout, DateTimeOffset now) =>
        ParseStream(videoId, ParseInfo(videoId, stdout), now);

    /// <summary>The track's info JSON: the last line of the <c>-j</c> output that is an object.</summary>
    public static JsonObject ParseInfo(string videoId, string stdout)
    {
        var line = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault(l => l.StartsWith('{'))
            ?? throw new StreamResolutionException(videoId, "yt-dlp returned no stream information.");

        try
        {
            return JsonNode.Parse(line) as JsonObject
                ?? throw new StreamResolutionException(videoId, "yt-dlp returned unexpected output.");
        }
        catch (JsonException ex)
        {
            throw new StreamResolutionException(videoId, "yt-dlp returned unreadable output.", ex);
        }
    }

    /// <summary>The format yt-dlp selected (merged into the info's top level), as it is.</summary>
    public static ResolvedStream ParseStream(string videoId, JsonObject root, DateTimeOffset now)
    {
        // Only a video+audio merge lacks a top-level url.
        var format = root;
        if (Text(root["url"]) is null && root["requested_formats"] is JsonArray requested)
        {
            format = requested.OfType<JsonObject>().FirstOrDefault(f => Text(f["vcodec"]) == "none")
                ?? requested.OfType<JsonObject>().FirstOrDefault()
                ?? root;
        }

        return ParseAudioFormat(videoId, root, format, now)
            ?? throw new StreamResolutionException(videoId, "yt-dlp did not return a playable URL.");
    }

    /// <summary>
    /// The audio to play (<see cref="AudioFormats.Plan"/>): the format yt-dlp selected when Windows plays it over HTTP,
    /// otherwise the best one it does, with <see cref="ResolvedStream.Fallback"/> leading to the best of each other kind.
    /// When the list has nothing Windows plays, yt-dlp's selection as it is (<see cref="ParseStream(string, JsonObject, DateTimeOffset)"/>).
    /// </summary>
    public static ResolvedStream ParseAudio(string videoId, JsonObject root, DateTimeOffset now)
    {
        ResolvedStream? stream = null;
        foreach (var format in AudioFormats.Plan(root, Formats(root)).Reverse())
        {
            if (ParseAudioFormat(videoId, root, format, now) is { } parsed)
            {
                stream = parsed with { Fallback = stream };
            }
        }

        return stream ?? ParseStream(videoId, root, now);
    }

    /// <summary>Whether the info lists audio that Windows plays over HTTP (<see cref="AudioFormats.Plan"/> isn't empty).</summary>
    public static bool HasPlayableAudio(JsonObject root) => AudioFormats.Plan(root, Formats(root)).Count > 0;

    private static IEnumerable<JsonObject> Formats(JsonObject root) =>
        root["formats"] is JsonArray list ? list.OfType<JsonObject>() : [];

    /// <summary>
    /// The video-only formats in yt-dlp's <c>formats</c> list that Windows plays over HTTP, no taller than
    /// <see cref="VideoFormats.MaxHeight"/>. yt-dlp lists every format whatever <c>-f</c> picked, so the audio resolve
    /// brings these along for free.
    /// </summary>
    /// <remarks>
    /// Only 8-bit VP9 in WebM qualifies. YouTube's MP4 video is fragmented (DASH) like its M4A audio: Media Foundation
    /// ends H.264 at once and stalls on AV1 after the first seek (verified 2026-10-12). VP9 profile 2 (vp09.02) is HDR.
    /// </remarks>
    public static IReadOnlyList<ResolvedStream> ParseVideoFormats(string videoId, JsonObject root, DateTimeOffset now)
    {
        if (root["formats"] is not JsonArray formats)
        {
            return [];
        }

        var seconds = Number(root["duration"]);
        TimeSpan? duration = seconds is > 0 ? TimeSpan.FromSeconds(seconds.Value) : null;
        var streams = new List<ResolvedStream>();
        foreach (var format in formats.OfType<JsonObject>())
        {
            if (ParseVideoFormat(videoId, root, format, duration, now) is { } stream)
            {
                streams.Add(stream);
            }
        }

        return streams;
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

    /// <summary>
    /// Why yt-dlp found no stream, from its last error line: <see cref="StreamFailureReason.AgeRestricted"/> for YouTube's
    /// age gate ("Sign in to confirm your age"), whether yt-dlp ran with the account or without.
    /// </summary>
    public static StreamFailureReason ClassifyError(string stderr) =>
        FindError(stderr) is { } error && AgeRestrictedRegex().IsMatch(error) ? StreamFailureReason.AgeRestricted : StreamFailureReason.Other;

    // yt-dlp copies available_at onto every format of a player response: when it was fetched, plus the pre-roll ad the
    // player would have shown. Until then googlevideo answers HTTP 403. Without an ad it is the fetch time itself (at
    // most the info's epoch): nothing to wait for.
    private static DateTimeOffset? ParseAvailableAt(JsonObject format, JsonObject root) =>
        Number(format["available_at"] ?? root["available_at"]) is { } seconds and > 0
        && !(Number(root["epoch"]) is { } epoch && seconds <= epoch)
            ? DateTimeOffset.FromUnixTimeSeconds((long)seconds)
            : null;

    private static ResolvedStream? ParseAudioFormat(string videoId, JsonObject root, JsonObject format, DateTimeOffset now)
    {
        if (!Uri.TryCreate(Text(format["url"]), UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var bitrate = Number(format["abr"]) ?? Number(format["tbr"]);
        var duration = Number(root["duration"]);
        return new ResolvedStream
        {
            VideoId = videoId,
            Url = url,
            Container = Text(format["ext"]),
            Codec = Text(format["acodec"]) is { } codec && codec != "none" ? codec : null,
            BitrateKbps = bitrate is > 0 ? (int)Math.Round(bitrate.Value) : null,
            Duration = duration is > 0 ? TimeSpan.FromSeconds(duration.Value) : null,
            ExpiresAt = ParseExpiry(url) ?? now + DefaultLifetime,
            AvailableAt = ParseAvailableAt(format, root),
            Headers = Headers(format["http_headers"] ?? root["http_headers"]),
        };
    }

    private static ResolvedStream? ParseVideoFormat(string videoId, JsonObject root, JsonObject format, TimeSpan? duration, DateTimeOffset now)
    {
        var codec = Text(format["vcodec"]);
        var width = Number(format["width"]) ?? 0;
        var height = Number(format["height"]) ?? 0;
        if (codec is null
            || !(codec == "vp9" || codec.StartsWith("vp09.00", StringComparison.Ordinal))
            || Text(format["acodec"]) != "none"
            || Text(format["ext"]) != "webm"
            || Text(format["protocol"]) is not ("https" or "http")
            || Text(format["dynamic_range"]) is not (null or "SDR")
            || (format["has_drm"] is JsonValue drm && drm.TryGetValue<bool>(out var hasDrm) && hasDrm)
            || width <= 0
            || height <= 0
            || Math.Min(width, height) > VideoFormats.MaxHeight
            || !Uri.TryCreate(Text(format["url"]), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var bitrate = Number(format["vbr"]) ?? Number(format["tbr"]);
        var fps = Number(format["fps"]);
        return new ResolvedStream
        {
            VideoId = videoId,
            Url = url,
            Container = "webm",
            Codec = codec,
            BitrateKbps = bitrate is > 0 ? (int)Math.Round(bitrate.Value) : null,
            Duration = duration,
            ExpiresAt = ParseExpiry(url) ?? now + DefaultLifetime,
            Width = (int)width,
            Height = (int)height,
            FrameRate = fps is > 0 ? fps : null,
            AvailableAt = ParseAvailableAt(format, root),
            Headers = Headers(format["http_headers"] ?? root["http_headers"]),
        };
    }

    private static Dictionary<string, string> Headers(JsonNode? node)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (node is JsonObject headerObject)
        {
            foreach (var (name, value) in headerObject)
            {
                if (Text(value) is { } text)
                {
                    headers[name] = text;
                }
            }
        }

        return headers;
    }

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

    // "Sign in to confirm your age. This video may be inappropriate for some users." (and the embed player's wording).
    [GeneratedRegex(@"confirm your age|age[- ]restricted|inappropriate for some users", RegexOptions.IgnoreCase)]
    private static partial Regex AgeRestrictedRegex();
}
