using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HushMusic.Auth;

/// <summary>
/// The cookie-auth Authorization header, exactly as ytmusicapi helpers.py:get_authorization builds it:
/// <c>SAPISIDHASH {ts}_{sha1hex("{ts} {SAPISID} {origin}")}</c>, recomputed for every request.
/// Browsers now also send SAPISID1PHASH / SAPISID3PHASH parts; ytmusicapi does not and the single part still works
/// (docs/innertube-requests.md 4.3 has the yt-dlp variant if that ever changes).
/// </summary>
internal static class SapisidHash
{
    public const string Origin = "https://music.youtube.com";

    public static string Compute(string sapisid, long unixTimeSeconds, string origin = Origin)
    {
        var timestamp = unixTimeSeconds.ToString(CultureInfo.InvariantCulture);
        var input = Encoding.UTF8.GetBytes(timestamp + " " + sapisid + " " + origin);

        // SHA-1 is what the protocol mandates; it is a request signature here, not password storage.
#pragma warning disable CA5350
        var digest = SHA1.HashData(input);
#pragma warning restore CA5350

        return "SAPISIDHASH " + timestamp + "_" + Convert.ToHexStringLower(digest);
    }
}
