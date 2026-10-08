using System.Text;
using HushMusic.Core.Abstractions;

namespace HushMusic.Auth.Cookies;

/// <summary>Builds, validates and serializes the youtube.com cookie jar. Pure functions, no I/O.</summary>
internal static class CookieJar
{
    /// <summary>ytmusicapi hashes this cookie (helpers.py:sapisid_from_cookie).</summary>
    public const string PreferredSapisidCookie = "__Secure-3PAPISID";

    /// <summary>Not ytmusicapi: yt-dlp's SAPISIDHASH source. Normally carries the same value as __Secure-3PAPISID.</summary>
    public const string FallbackSapisidCookie = "SAPISID";

    /// <summary>Domain assumed for cookies pasted as a header, where the real domain is unknown.</summary>
    public const string PastedCookieDomain = ".youtube.com";

    /// <summary>Host for pasted "__Host-" cookies, which are host-only by definition.</summary>
    public const string PastedHostOnlyDomain = "music.youtube.com";

    private const string BaseDomain = "youtube.com";

    /// <summary>True for youtube.com and any subdomain of it.</summary>
    public static bool IsYouTubeHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.Equals(BaseDomain, StringComparison.OrdinalIgnoreCase)
            || host.EndsWith("." + BaseDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>True when a cookie domain (with or without the leading dot) belongs to youtube.com.</summary>
    public static bool IsYouTubeCookieDomain(string? domain) => IsYouTubeHost(domain?.Trim().TrimStart('.'));

    /// <summary>
    /// Keeps the youtube.com cookies (first wins per domain/path/name) and normalizes them.
    /// <paramref name="skipped"/> counts cookies dropped for a foreign domain or an invalid name.
    /// </summary>
    public static List<StoredCookie> FromBrowserCookies(IEnumerable<BrowserCookie> cookies, out int skipped)
    {
        skipped = 0;
        var result = new List<StoredCookie>();
        var seen = new HashSet<(string Domain, string Path, string Name)>();

        foreach (var cookie in cookies)
        {
            if (cookie is null || !IsValidName(cookie.Name) || !IsYouTubeCookieDomain(cookie.Domain))
            {
                skipped++;
                continue;
            }

            var domain = cookie.Domain.Trim().ToLowerInvariant();
            var path = string.IsNullOrWhiteSpace(cookie.Path) ? "/" : cookie.Path.Trim();
            if (!seen.Add((domain, path, cookie.Name)))
            {
                skipped++;
                continue;
            }

            // WebView2 reports session cookies as expires = -1; callers may map that to the epoch or earlier.
            var expires = cookie.Expires is { } e && e.ToUnixTimeSeconds() > 0 ? e : (DateTimeOffset?)null;

            result.Add(new StoredCookie(
                cookie.Name,
                SanitizeValue(cookie.Value ?? string.Empty),
                domain,
                path,
                expires,
                cookie.IsSecure,
                cookie.IsHttpOnly));
        }

        return result;
    }

    /// <summary>
    /// Parses a pasted <c>Cookie</c> header: <c>name=value; name2=value2</c>, optionally prefixed with
    /// "Cookie:", wrapped in quotes, or spread over several lines. Lines of other headers in a pasted
    /// header block are ignored because their "names" are not valid cookie names.
    /// </summary>
    public static List<StoredCookie> ParseCookieHeader(string text)
    {
        var result = new List<StoredCookie>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var trimmed = StripOuterQuotes(text.Trim());
        foreach (var rawLine in trimmed.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            {
                line = line["cookie:".Length..];
            }

            foreach (var segment in line.Split(';'))
            {
                var pair = segment.Trim();
                var equals = pair.IndexOf('=');
                if (equals <= 0)
                {
                    continue;
                }

                var name = pair[..equals].Trim();
                var value = pair[(equals + 1)..].Trim();
                if (!IsValidName(name) || !seen.Add(name))
                {
                    continue;
                }

                var hostOnly = name.StartsWith("__Host-", StringComparison.Ordinal);
                result.Add(new StoredCookie(
                    name,
                    SanitizeValue(value),
                    hostOnly ? PastedHostOnlyDomain : PastedCookieDomain,
                    "/",
                    Expires: null,
                    Secure: true,
                    HttpOnly: false));
            }
        }

        return result;
    }

    /// <summary>
    /// The value to hash for SAPISIDHASH: __Secure-3PAPISID (ytmusicapi), else SAPISID.
    /// A cookie on .youtube.com wins over a host-only duplicate. Quotes are stripped like ytmusicapi does.
    /// </summary>
    public static string? FindSapisid(IReadOnlyList<StoredCookie> cookies) =>
        FindValue(cookies, PreferredSapisidCookie) ?? FindValue(cookies, FallbackSapisidCookie);

    /// <summary>The whole jar as one header value, in jar order: <c>name=value; name2=value2</c>.</summary>
    public static string BuildCookieHeader(IReadOnlyList<StoredCookie> cookies)
    {
        var builder = new StringBuilder();
        foreach (var cookie in cookies)
        {
            if (builder.Length > 0)
            {
                builder.Append("; ");
            }

            builder.Append(cookie.Name).Append('=').Append(cookie.Value);
        }

        return builder.ToString();
    }

    /// <summary>RFC 6265 token: visible ASCII without separators.</summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c <= 0x20 || c >= 0x7F || "()<>@,;:\\\"/[]?={}".Contains(c))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Percent-encodes characters that cannot go into an HTTP header or a tab-separated cookies.txt line
    /// (ytmusicapi auth_parse.py does the same for non-printable characters, fix for #856).
    /// Valid browser cookie values pass through unchanged.
    /// </summary>
    public static string SanitizeValue(string value)
    {
        var needsEncoding = false;
        foreach (var c in value)
        {
            if (NeedsEncoding(c))
            {
                needsEncoding = true;
                break;
            }
        }

        if (!needsEncoding)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 16);
        Span<byte> utf8 = stackalloc byte[4];
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!NeedsEncoding(c))
            {
                builder.Append(c);
                continue;
            }

            var length = char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])
                ? Encoding.UTF8.GetBytes(value.AsSpan(i++, 2), utf8)
                : Encoding.UTF8.GetBytes(value.AsSpan(i, 1), utf8);
            foreach (var b in utf8[..length])
            {
                builder.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static bool NeedsEncoding(char c) => c < 0x20 || c >= 0x7F || c == ';';

    private static string? FindValue(IReadOnlyList<StoredCookie> cookies, string name)
    {
        StoredCookie? match = null;
        foreach (var cookie in cookies)
        {
            if (!cookie.Name.Equals(name, StringComparison.Ordinal))
            {
                continue;
            }

            if (match is null || (cookie.Domain.StartsWith('.') && !match.Domain.StartsWith('.')))
            {
                match = cookie;
            }
        }

        var value = match?.Value.Replace("\"", string.Empty, StringComparison.Ordinal);
        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static string StripOuterQuotes(string text)
    {
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
        {
            return text[1..^1].Trim();
        }

        return text;
    }
}
