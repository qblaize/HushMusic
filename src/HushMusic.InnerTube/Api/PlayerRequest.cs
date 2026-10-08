using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Api;

/// <summary>The <c>player</c> request exactly as ytmusicapi <c>get_song</c> builds it.</summary>
internal static class PlayerRequest
{
    public const string Endpoint = "player";

    public static JsonObject Body(string videoId, DateTimeOffset now) => new()
    {
        ["playbackContext"] = new JsonObject
        {
            ["contentPlaybackContext"] = new JsonObject { ["signatureTimestamp"] = SignatureTimestamp(now) },
        },

        // snake_case, unlike every other InnerTube field; ytmusicapi sends it this way.
        ["video_id"] = videoId,
    };

    /// <summary>ytmusicapi <c>get_datestamp() - 1</c>: whole days since 1970-01-01 UTC, minus one.</summary>
    public static int SignatureTimestamp(DateTimeOffset now) => (now.UtcDateTime - DateTime.UnixEpoch).Days - 1;
}
