namespace HushMusic.Core.Radio;

/// <summary>
/// When to reconnect a live radio stream that dropped or failed (used by the player; not thread-safe, the caller locks).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Before the station has played at all, one quick retry: a dead or unsupported stream should fail fast.</item>
/// <item>After it has played, up to <see cref="MaxAttemptsAfterDrop"/> reconnects with a doubling delay (1, 2, 4, 8 s):
/// Icecast servers drop listeners briefly (DJ handovers, restarts, network blips).</item>
/// <item>A connection that played for <see cref="StableAfter"/> earns a fresh set of attempts, so a drop hours later is
/// treated like the first one.</item>
/// <item>Fatal errors (refused URL, unsupported format) before the first play are not retried.</item>
/// </list>
/// </remarks>
public sealed class LiveReconnectPolicy(TimeProvider time)
{
    public const int MaxAttemptsBeforeFirstPlay = 1;

    public const int MaxAttemptsAfterDrop = 4;

    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan FirstPlayRetryDelay = TimeSpan.FromMilliseconds(750);

    private int _attempts;
    private bool _hasPlayed;
    private DateTimeOffset? _playingSince;

    /// <summary>Reconnect attempts since the last stable connection.</summary>
    public int Attempts => _attempts;

    public bool HasPlayed => _hasPlayed;

    /// <summary>A new station starts: forget everything.</summary>
    public void Reset()
    {
        _attempts = 0;
        _hasPlayed = false;
        _playingSince = null;
    }

    /// <summary>The user pressed play again: new attempts, but the station is known to work.</summary>
    public void ResetAttempts()
    {
        _attempts = 0;
        _playingSince = null;
    }

    /// <summary>Audio is playing on the current connection.</summary>
    public void OnPlaying()
    {
        _hasPlayed = true;
        _playingSince ??= time.GetUtcNow();
    }

    /// <summary>The connection failed or ended. Returns how long to wait before reconnecting, or null to give up.</summary>
    public TimeSpan? OnFailure(bool fatal)
    {
        if (_playingSince is { } since && time.GetUtcNow() - since >= StableAfter)
        {
            _attempts = 0;
        }

        _playingSince = null;
        if (fatal && !_hasPlayed)
        {
            return null;
        }

        _attempts++;
        if (!_hasPlayed)
        {
            return _attempts <= MaxAttemptsBeforeFirstPlay ? FirstPlayRetryDelay : null;
        }

        return _attempts <= MaxAttemptsAfterDrop ? TimeSpan.FromSeconds(1 << (_attempts - 1)) : null;
    }
}
