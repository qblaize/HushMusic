using System.Globalization;
using System.Text.Json.Nodes;

namespace HushMusic.InnerTube.Http;

/// <summary>Which InnerTube client a request identifies as in <c>context.client</c>.</summary>
internal enum ClientProfile
{
    /// <summary>WEB_REMIX, the music.youtube.com web app. Every request unless stated otherwise.</summary>
    Web,

    /// <summary>
    /// ANDROID_MUSIC, ytmusicapi <c>as_mobile()</c>: only the client name and version change, headers stay the
    /// web ones. ytmusicapi uses it for one call only, <c>get_lyrics(timestamps=True)</c>.
    /// </summary>
    Mobile,
}

/// <summary>Builds the <c>context</c> object merged into every InnerTube request body.</summary>
internal static class ClientContext
{
    // ytmusicapi (helpers.initialize_context) has no pinned version: it sends "1." + today's UTC date + ".01.00".
    public static string ClientVersion(DateTimeOffset now) =>
        "1." + now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".01.00";

    /// <param name="location">ISO country code for <c>gl</c>; null or empty lets the server decide (ytmusicapi default).</param>
    /// <param name="profile">Client to identify as. <c>hl</c>, <c>gl</c> and <c>user</c> are the same for every profile.</param>
    public static JsonObject Create(DateTimeOffset now, string? location, ClientProfile profile = ClientProfile.Web)
    {
        var client = profile switch
        {
            ClientProfile.Mobile => new JsonObject
            {
                ["clientName"] = InnerTubeConstants.MobileClientName,
                ["clientVersion"] = InnerTubeConstants.MobileClientVersion,
            },
            _ => new JsonObject
            {
                ["clientName"] = InnerTubeConstants.ClientName,
                ["clientVersion"] = ClientVersion(now),
            },
        };

        if (!string.IsNullOrWhiteSpace(location))
        {
            client["gl"] = location.Trim().ToUpperInvariant();
        }

        client["hl"] = InnerTubeConstants.Language;

        return new JsonObject
        {
            ["client"] = client,
            ["user"] = new JsonObject(),
        };
    }
}
