using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace HushMusic.Auth.Cookies;

/// <summary>Writes the jar as a Netscape cookies.txt file, the only cookie format yt-dlp's <c>--cookies</c> accepts.</summary>
internal static class NetscapeCookieFile
{
    // yt-dlp (http.cookiejar.MozillaCookieJar) rejects the file unless the first line matches "#( Netscape)? HTTP Cookie File".
    private const string Header = "# Netscape HTTP Cookie File\n# Written by HushMusic for yt-dlp. Deleted after use.\n\n";

    // curl / yt-dlp convention: HttpOnly cookies are written as comment-like lines with this prefix.
    private const string HttpOnlyPrefix = "#HttpOnly_";

    /// <summary>
    /// One line per cookie: domain, include-subdomains, path, secure, expiry (unix seconds, 0 = session), name, value.
    /// MozillaCookieJar asserts that include-subdomains is TRUE exactly when the domain starts with '.'.
    /// </summary>
    public static string Format(IReadOnlyList<StoredCookie> cookies)
    {
        var builder = new StringBuilder(Header);
        var seen = new HashSet<(string, string, string)>();

        foreach (var cookie in cookies)
        {
            var domain = cookie.Domain;
            var path = string.IsNullOrEmpty(cookie.Path) ? "/" : cookie.Path;
            if (!seen.Add((domain.ToLowerInvariant(), path, cookie.Name)))
            {
                continue;
            }

            var expires = cookie.Expires is { } e ? Math.Max(0, e.ToUnixTimeSeconds()) : 0;

            if (cookie.HttpOnly)
            {
                builder.Append(HttpOnlyPrefix);
            }

            builder.Append(domain).Append('\t')
                .Append(domain.StartsWith('.') ? "TRUE" : "FALSE").Append('\t')
                .Append(path).Append('\t')
                .Append(cookie.Secure ? "TRUE" : "FALSE").Append('\t')
                .Append(expires.ToString(CultureInfo.InvariantCulture)).Append('\t')
                .Append(cookie.Name).Append('\t')
                .Append(cookie.Value).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates <paramref name="path"/> (must not exist) with a protected DACL that grants only the current user
    /// access, so the file is never readable by anyone else, not even for a moment after creation.
    /// </summary>
    public static async Task WriteRestrictedAsync(string path, string content, CancellationToken cancellationToken)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        using (var identity = WindowsIdentity.GetCurrent())
        {
            var user = identity.User ?? throw new InvalidOperationException("The current Windows user has no SID.");
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
        await using var stream = new FileInfo(path).Create(
            FileMode.CreateNew,
            FileSystemRights.FullControl,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous,
            security);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
