using System.Security.Cryptography;
using System.Text;

namespace HushMusic.Core.Features.LastFm;

/// <summary>Last.fm request signing (api_sig), as described at last.fm/api/authspec.</summary>
public static class LastFmSignature
{
    /// <summary>
    /// Lowercase hex MD5 (UTF-8) of every parameter except <c>format</c> and <c>callback</c>, sorted by name (ordinal),
    /// written as name + value, followed by the shared secret.
    /// </summary>
    public static string Sign(IEnumerable<KeyValuePair<string, string>> parameters, string sharedSecret)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        ArgumentNullException.ThrowIfNull(sharedSecret);

        var builder = new StringBuilder();
        foreach (var (name, value) in parameters
            .Where(p => p.Key is not ("format" or "callback"))
            .OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            builder.Append(name).Append(value);
        }

        builder.Append(sharedSecret);

        // MD5 is mandated by the Last.fm API; it is a request checksum, not a password hash.
        return Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
