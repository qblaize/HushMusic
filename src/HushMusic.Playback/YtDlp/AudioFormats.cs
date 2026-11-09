using System.Text.Json.Nodes;

namespace HushMusic.Playback.YtDlp;

/// <summary>The kinds of YouTube audio that Windows MediaPlayer plays over HTTP, best first.</summary>
internal enum AudioKind
{
    /// <summary>Audio-only WebM with Opus (itags 249–251): the normal choice.</summary>
    WebmOpus,

    /// <summary>Audio-only WebM with another codec (Vorbis on old uploads).</summary>
    WebmOther,

    /// <summary>
    /// A whole file with sound and picture (itag 18, 360p MP4 with AAC). Unlike the DASH formats it isn't fragmented, so
    /// Media Foundation plays it; the picture is decoded for nothing, so it is the last resort.
    /// </summary>
    Progressive,
}

/// <summary>
/// Which of the formats in yt-dlp's <c>-j</c> output to play, and what to try next when Windows can't play it.
/// </summary>
/// <remarks>
/// Left out: YouTube's audio-only MP4 (M4A, also Dolby "ec-3" and MPEG-H "mhm1"), which is fragmented: Media Foundation
/// opens it over HTTP and then ends at once; manifests (HLS, DASH segments), which aren't files; DRM; and formats yt-dlp
/// listed without a URL (it does that when a client needs a token it didn't have).
/// </remarks>
internal static class AudioFormats
{
    /// <summary>The kind of <paramref name="format"/>, or null when Windows can't play it as a file over HTTP.</summary>
    public static AudioKind? Classify(JsonObject format)
    {
        var audio = Text(format["acodec"]);
        if (audio is null or "none"
            || Text(format["protocol"]) is not ("https" or "http")
            || format["has_drm"] is JsonValue drm && drm.TryGetValue<bool>(out var hasDrm) && hasDrm
            || !Uri.TryCreate(Text(format["url"]), UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
        {
            return null;
        }

        var ext = Text(format["ext"]);
        if (Text(format["vcodec"]) is null or "none")
        {
            return ext != "webm" ? null : audio == "opus" ? AudioKind.WebmOpus : AudioKind.WebmOther;
        }

        // Sound and picture together: only the old progressive formats are whole files (DASH video has no sound anyway).
        var dash = Text(format["container"])?.EndsWith("_dash", StringComparison.Ordinal) == true;
        return ext is "mp4" or "webm" && !dash ? AudioKind.Progressive : null;
    }

    /// <summary>
    /// The formats to try, in order: the one yt-dlp selected (<paramref name="selected"/>, its fields merged into the
    /// info's top level) when Windows can play it, otherwise the best playable one; then the best of each other kind.
    /// Within a kind: the full-range version before YouTube's compressed "DRC" one, the original language before dubs,
    /// then the higher bitrate. Empty when nothing is playable.
    /// </summary>
    public static IReadOnlyList<JsonObject> Plan(JsonObject selected, IEnumerable<JsonObject> formats)
    {
        var ranked = formats
            .Select(f => (Format: f, Kind: Classify(f)))
            .Where(c => c.Kind is not null)
            .OrderBy(c => c.Kind)
            .ThenBy(c => IsDrc(c.Format) ? 1 : 0)
            .ThenByDescending(c => Number(c.Format["language_preference"]) ?? 0)
            .ThenByDescending(c => Number(c.Format["abr"]) ?? Number(c.Format["tbr"]) ?? 0)
            .ToList();

        var plan = new List<JsonObject>(3);
        var selectedKind = Classify(selected);
        if (selectedKind is not null)
        {
            plan.Add(selected);
        }

        foreach (var kind in ranked.GroupBy(c => c.Kind))
        {
            if (kind.Key != selectedKind)
            {
                plan.Add(kind.First().Format);
            }
        }

        return plan;
    }

    private static bool IsDrc(JsonObject format) =>
        Text(format["format_id"])?.EndsWith("-drc", StringComparison.OrdinalIgnoreCase) == true
        || Text(format["format_note"])?.Contains("DRC", StringComparison.Ordinal) == true;

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrEmpty(text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : null;
}
