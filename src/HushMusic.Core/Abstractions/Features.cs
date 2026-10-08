namespace HushMusic.Core.Abstractions;

/// <summary>Pauses playback after a delay or at the end of the current track. Events are raised on a background thread.</summary>
public interface ISleepTimer
{
    bool IsActive { get; }

    /// <summary>When the timer fires. Null when off or when it stops at the end of the track.</summary>
    DateTimeOffset? EndsAt { get; }

    bool StopsAtEndOfTrack { get; }

    /// <summary>Started, cancelled or fired.</summary>
    event EventHandler? Changed;

    /// <summary>Fades out and pauses after <paramref name="duration"/>. Replaces any running timer.</summary>
    void Start(TimeSpan duration);

    /// <summary>Pauses when the current track ends. Replaces any running timer.</summary>
    void StopAtEndOfTrack();

    void Cancel();
}

public enum LastFmState
{
    /// <summary>No account connected.</summary>
    Disconnected,

    /// <summary>The browser was opened; waiting for the user to approve the app on last.fm.</summary>
    AwaitingApproval,

    Connected,
}

/// <summary>
/// Last.fm account link and scrobbling. The user's own API key and shared secret (last.fm/api/account/create) and the
/// session key are kept in <see cref="ISecretStore"/>. Events are raised on a background thread.
/// </summary>
public interface ILastFmService
{
    LastFmState State { get; }

    /// <summary>The connected Last.fm user name.</summary>
    string? UserName { get; }

    /// <summary>Scrobbles waiting to be sent (offline or failed).</summary>
    int PendingScrobbles { get; }

    event EventHandler? StateChanged;

    /// <summary>
    /// Stores the API credentials, requests an auth token and returns the last.fm page where the user approves the app.
    /// The caller opens it in the browser and then calls <see cref="CompleteConnectAsync"/>.
    /// </summary>
    Task<Uri> BeginConnectAsync(string apiKey, string sharedSecret, CancellationToken cancellationToken = default);

    /// <summary>Exchanges the approved token for a session. Returns false while the user has not approved yet.</summary>
    Task<bool> CompleteConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>Forgets the session key (the API credentials too).</summary>
    Task DisconnectAsync(CancellationToken cancellationToken = default);
}
