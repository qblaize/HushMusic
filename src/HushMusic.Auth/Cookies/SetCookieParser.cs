using System.Globalization;

namespace HushMusic.Auth.Cookies;

/// <summary>A parsed <c>Set-Cookie</c> header. <see cref="Domain"/> is null for a host-only cookie.</summary>
internal readonly record struct SetCookie(
    string Name,
    string Value,
    string? Domain,
    string? Path,
    DateTimeOffset? Expires,
    bool IsDeletion);

/// <summary>Minimal RFC 6265 Set-Cookie parser: enough to pick up rotated values of cookies we already hold.</summary>
internal static class SetCookieParser
{
    private static readonly string[] ExpiresFormats =
    [
        "ddd, dd-MMM-yyyy HH:mm:ss 'GMT'",
        "ddd, dd MMM yyyy HH:mm:ss 'GMT'",
        "ddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "dddd, dd-MMM-yy HH:mm:ss 'GMT'",
        "ddd MMM d HH:mm:ss yyyy",
    ];

    public static bool TryParse(string header, DateTimeOffset now, out SetCookie cookie)
    {
        cookie = default;
        if (string.IsNullOrWhiteSpace(header))
        {
            return false;
        }

        var parts = header.Split(';');
        var pair = parts[0];
        var equals = pair.IndexOf('=');
        if (equals <= 0)
        {
            return false;
        }

        var name = pair[..equals].Trim();
        var value = pair[(equals + 1)..].Trim();
        if (!CookieJar.IsValidName(name))
        {
            return false;
        }

        string? domain = null;
        string? path = null;
        DateTimeOffset? expires = null;
        DateTimeOffset? maxAgeExpires = null;

        for (var i = 1; i < parts.Length; i++)
        {
            var attribute = parts[i].Trim();
            var attributeEquals = attribute.IndexOf('=');
            var key = attributeEquals < 0 ? attribute : attribute[..attributeEquals].Trim();
            var attributeValue = attributeEquals < 0 ? string.Empty : attribute[(attributeEquals + 1)..].Trim();

            if (key.Equals("Domain", StringComparison.OrdinalIgnoreCase))
            {
                var bare = attributeValue.TrimStart('.').ToLowerInvariant();
                if (bare.Length > 0)
                {
                    domain = "." + bare;
                }
            }
            else if (key.Equals("Path", StringComparison.OrdinalIgnoreCase))
            {
                if (attributeValue.StartsWith('/'))
                {
                    path = attributeValue;
                }
            }
            else if (key.Equals("Expires", StringComparison.OrdinalIgnoreCase))
            {
                if (TryParseExpires(attributeValue, out var parsed))
                {
                    expires = parsed;
                }
            }
            else if (key.Equals("Max-Age", StringComparison.OrdinalIgnoreCase))
            {
                if (long.TryParse(attributeValue, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var seconds))
                {
                    maxAgeExpires = seconds <= 0
                        ? DateTimeOffset.MinValue
                        : now.AddSeconds(Math.Min(seconds, 400L * 24 * 3600));
                }
            }
        }

        // Max-Age wins over Expires (RFC 6265 5.3 step 3).
        var effectiveExpires = maxAgeExpires ?? expires;
        var isDeletion = effectiveExpires is { } e && e <= now;
        cookie = new SetCookie(name, value, domain, path, isDeletion ? null : effectiveExpires, isDeletion);
        return true;
    }

    private static bool TryParseExpires(string text, out DateTimeOffset result)
    {
        const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;
        return DateTimeOffset.TryParseExact(text, ExpiresFormats, CultureInfo.InvariantCulture, Styles, out result)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out result);
    }
}
