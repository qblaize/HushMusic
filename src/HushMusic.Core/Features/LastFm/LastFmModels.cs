using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("HushMusic.Core.Tests")]

namespace HushMusic.Core.Features.LastFm;

/// <summary>One listen, as sent to track.scrobble (and, without the timestamp, to track.updateNowPlaying).</summary>
/// <param name="Timestamp">UTC unix seconds of when the track started playing.</param>
public sealed record LastFmScrobble(string Artist, string Track, string? Album, long Timestamp, int? DurationSeconds);

/// <summary>The user's own API account (last.fm/api/account/create).</summary>
internal sealed record LastFmCredentials(string ApiKey, string SharedSecret);

internal sealed record LastFmSession(string UserName, string Key);

internal sealed record LastFmScrobbleResult(int Accepted, int Ignored);

/// <summary>An error returned by the Last.fm API (<c>{"error": code, "message": ...}</c>).</summary>
public sealed class LastFmException(int code, string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    public const int InvalidParameters = 6;
    public const int OperationFailed = 8;
    public const int InvalidSessionKey = 9;
    public const int InvalidApiKey = 10;
    public const int ServiceOffline = 11;
    public const int TokenNotAuthorized = 14;
    public const int TokenExpired = 15;
    public const int TemporarilyUnavailable = 16;
    public const int SuspendedApiKey = 26;
    public const int RateLimitExceeded = 29;

    public int Code { get; } = code;

    /// <summary>Worth retrying later with the same request.</summary>
    public bool IsTransient => Code is OperationFailed or ServiceOffline or TemporarilyUnavailable or RateLimitExceeded;
}
