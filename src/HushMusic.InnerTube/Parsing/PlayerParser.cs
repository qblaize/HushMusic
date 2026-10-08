using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// <c>player</c> response (request as ytmusicapi get_song). Only the loudness is read: stream URLs come from yt-dlp.
/// </summary>
internal static class PlayerParser
{
    private const string Page = "Player";

    /// <summary>
    /// Track loudness relative to YouTube's normalization target, in dB: <c>playerConfig.audioConfig.loudnessDb</c>.
    /// Null when the response has none; unplayable and age-gated tracks come back without <c>playerConfig</c>.
    /// </summary>
    public static double? ParseLoudnessDb(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);

        var audio = response.Obj("playerConfig", "audioConfig");
        if (audio.Double("loudnessDb") is { } loudness)
        {
            return loudness;
        }

        // perceptualLoudnessDb is the track's absolute loudness (LKFS), not relative like loudnessDb; in every captured
        // response loudnessDb == perceptualLoudnessDb - loudnessTargetLkfs (e.g. -8.08 - -7 = -1.08).
        if (audio.Double("perceptualLoudnessDb") is { } perceptual && audio.Double("loudnessTargetLkfs") is { } target)
        {
            return perceptual - target;
        }

        var scope = new ParseScope(logger, Page);
        var status = response.Str("playabilityStatus", "status");
        if (status == "OK")
        {
            scope.MissingStructure("playerConfig.audioConfig.loudnessDb");
        }
        else
        {
            scope.IgnoreItem("playerConfig", $"no loudness, playability status {status ?? "missing"}");
        }

        return null;
    }
}
