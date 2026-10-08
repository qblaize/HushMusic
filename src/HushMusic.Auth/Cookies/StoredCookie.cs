using System.Text.Json.Serialization;

namespace HushMusic.Auth.Cookies;

/// <summary>One cookie of the signed-in jar. <see cref="Domain"/> starts with '.' for domain cookies, none for host-only.</summary>
internal sealed record StoredCookie(
    string Name,
    string Value,
    string Domain,
    string Path,
    DateTimeOffset? Expires,
    bool Secure,
    bool HttpOnly);

/// <summary>The plaintext that gets DPAPI-protected into session.bin.</summary>
internal sealed class SessionBlob
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public DateTimeOffset SavedAt { get; set; }

    public List<StoredCookie> Cookies { get; set; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SessionBlob))]
internal sealed partial class AuthJsonContext : JsonSerializerContext;
