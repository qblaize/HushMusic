namespace HushMusic.Core.Radio;

/// <summary>How a live stream stalled (see <see cref="LiveStallWatchdog"/>).</summary>
public enum LiveStall
{
    /// <summary>Connecting, opening or buffering went on for <see cref="LiveStallWatchdog.StartTimeout"/> without audio.</summary>
    NoAudio,

    /// <summary>Reported as playing, but the position didn't move for <see cref="LiveStallWatchdog.FrozenTimeout"/>.</summary>
    Frozen,
}

/// <summary>
/// Notices a live radio stream that hangs without failing: a server that accepts the connection and never sends
/// audio, or one that stops sending in the middle. Media Foundation reports neither (it keeps opening or buffering,
/// or even keeps saying "playing"), so the player would spin forever. Not thread-safe; the caller locks.
/// </summary>
/// <remarks>
/// A station normally plays 0.5–1.5 s after its stream is opened, so the timeouts leave a wide margin for slow servers.
/// The caller reports what the player is doing (<see cref="Wait"/>, <see cref="Playing"/>, <see cref="Sample"/>,
/// <see cref="Stop"/>), asks <see cref="Check"/> when <see cref="DueIn"/> has passed, and reconnects on a stall.
/// </remarks>
public sealed class LiveStallWatchdog(TimeProvider time)
{
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(12);

    public static readonly TimeSpan FrozenTimeout = TimeSpan.FromSeconds(9);

    private DateTimeOffset? _waitingSince;
    private DateTimeOffset? _movedAt;
    private TimeSpan _position;

    /// <summary>Watching a connection: waiting for audio, or playing.</summary>
    public bool IsWatching => _waitingSince is not null || _movedAt is not null;

    /// <summary>
    /// The stream is connecting, opening or buffering. The clock starts now unless it already runs (a stream that
    /// goes from opening to buffering doesn't get more time); <paramref name="restart"/> starts it again for a new
    /// connection.
    /// </summary>
    public void Wait(bool restart = false)
    {
        if (restart || _waitingSince is null)
        {
            _waitingSince = time.GetUtcNow();
        }

        _movedAt = null;
    }

    /// <summary>Reported as playing at <paramref name="position"/>; from now on the position has to keep moving.</summary>
    public void Playing(TimeSpan position)
    {
        if (_movedAt is null)
        {
            _waitingSince = null;
            _movedAt = time.GetUtcNow();
            _position = position;
            return;
        }

        Sample(position);
    }

    /// <summary>
    /// The position while playing. Any change counts as progress: some streams start their clock over. Returns true when
    /// it moved.
    /// </summary>
    public bool Sample(TimeSpan position)
    {
        if (_movedAt is null || position == _position)
        {
            return false;
        }

        _position = position;
        _movedAt = time.GetUtcNow();
        return true;
    }

    /// <summary>Nothing to watch: paused, stopped, failed, or another item.</summary>
    public void Stop()
    {
        _waitingSince = null;
        _movedAt = null;
    }

    /// <summary>The stall, if the stream has stalled; null while it is fine or nothing is watched.</summary>
    public LiveStall? Check()
    {
        var now = time.GetUtcNow();
        if (_waitingSince is { } since && now - since >= StartTimeout)
        {
            return LiveStall.NoAudio;
        }

        return _movedAt is { } moved && now - moved >= FrozenTimeout ? LiveStall.Frozen : null;
    }

    /// <summary>When <see cref="Check"/> could next find a stall; null when nothing is watched.</summary>
    public TimeSpan? DueIn()
    {
        var deadline = _waitingSince + StartTimeout ?? _movedAt + FrozenTimeout;
        if (deadline is not { } due)
        {
            return null;
        }

        var left = due - time.GetUtcNow();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}
