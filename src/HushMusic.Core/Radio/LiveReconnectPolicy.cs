namespace HushMusic.Core.Radio;

/// <summary>
/// When to reconnect a live radio stream that dropped, failed or stalled (used by the player; not thread-safe, the
/// caller locks).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Reconnects wait 1, 2, 5, then 10 s (<see cref="Delays"/>): Icecast servers drop listeners briefly (DJ
/// handovers, restarts, network blips), and a Bluetooth speaker or Wi-Fi coming back takes a few seconds.</item>
/// <item>After the station has played, up to <see cref="MaxAttempts"/> reconnects in a row; before it has played at all,
/// only <see cref="MaxAttemptsBeforeFirstPlay"/>, so a dead stream fails in reasonable time.</item>
/// <item>A connection that plays audio for <see cref="StableAfter"/> resets the count and the delay, so a drop hours
/// later is treated like the first one. One that drops sooner keeps counting: a stream that keeps dropping soon after it
/// starts is given up rather than reconnected forever. The time counts from the first audio to the last
/// (<see cref="OnPlaying"/>), not to when the loss was noticed.</item>
/// <item>Fatal errors (refused URL, unsupported format) before the first play are not retried.</item>
/// </list>
/// </remarks>
public sealed class LiveReconnectPolicy(TimeProvider time)
{
    public const int MaxAttemptsBeforeFirstPlay = 2;

    public const int MaxAttempts = 5;

    public static readonly TimeSpan StableAfter = TimeSpan.FromSeconds(30);

    /// <summary>The wait before reconnect 1, 2, 3, …; the last one repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> Delays =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)];

    private int _attempts;
    private bool _hasPlayed;
    private DateTimeOffset? _playingSince;
    private DateTimeOffset _playedUntil;

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

    /// <summary>Audio is playing on the current connection: call it as long as it does (each time the position moves).</summary>
    public void OnPlaying()
    {
        var now = time.GetUtcNow();
        _hasPlayed = true;
        _playingSince ??= now;
        _playedUntil = now;
    }

    /// <summary>The connection failed, ended or stalled. Returns how long to wait before reconnecting, or null to give up.</summary>
    public TimeSpan? OnFailure(bool fatal)
    {
        if (_playingSince is { } since && _playedUntil - since >= StableAfter)
        {
            _attempts = 0;
        }

        _playingSince = null;
        if (fatal && !_hasPlayed)
        {
            return null;
        }

        _attempts++;
        if (_attempts > (_hasPlayed ? MaxAttempts : MaxAttemptsBeforeFirstPlay))
        {
            return null;
        }

        return Delays[Math.Min(_attempts, Delays.Count) - 1];
    }
}
