namespace HushMusic.Core.Features.Discord;

/// <summary>
/// Status updates under Discord's SET_ACTIVITY rate limit (about 5 per 20 seconds): the latest update wins, one that
/// matches what Discord already shows isn't sent, and sends are at least <see cref="MinSpacing"/> apart. Not thread-safe.
/// </summary>
public sealed class PresenceCoalescer
{
    public static readonly TimeSpan MinSpacing = TimeSpan.FromSeconds(4);
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(20);
    public const int MaxPerWindow = 5;

    private readonly Queue<DateTimeOffset> _sends = new();

    // A fresh connection shows nothing, so "nothing" counts as sent.
    private string? _shown;
    private string? _pending;
    private bool _hasPending;

    /// <summary>An update is waiting to go out.</summary>
    public bool HasPending => _hasPending;

    /// <summary>The waiting update clears the status.</summary>
    public bool PendingIsClear => _hasPending && _pending is null;

    /// <summary>Makes <paramref name="activityJson"/> (null clears) the next update, replacing one not sent yet.</summary>
    public void Set(string? activityJson)
    {
        _pending = activityJson;
        _hasPending = !string.Equals(activityJson, _shown, StringComparison.Ordinal);
    }

    /// <summary>When the waiting update may go out (at the earliest <paramref name="now"/>), or null when none is waiting.</summary>
    public DateTimeOffset? DueAt(DateTimeOffset now)
    {
        if (!_hasPending)
        {
            return null;
        }

        Forget(now);
        var due = now;
        if (_sends.Count > 0)
        {
            var last = _sends.Last() + MinSpacing;
            due = last > due ? last : due;
        }

        if (_sends.Count >= MaxPerWindow)
        {
            var reopens = _sends.ElementAt(_sends.Count - MaxPerWindow) + Window;
            due = reopens > due ? reopens : due;
        }

        return due;
    }

    /// <summary>Takes the waiting update when it may go out at <paramref name="now"/>, and counts it as sent.</summary>
    public bool TryTake(DateTimeOffset now, out string? activityJson)
    {
        activityJson = null;
        if (DueAt(now) is not { } due || due > now)
        {
            return false;
        }

        activityJson = _pending;
        _shown = _pending;
        _hasPending = false;
        _sends.Enqueue(now);
        return true;
    }

    /// <summary>
    /// A new connection: Discord shows nothing for it yet, so <paramref name="current"/> (the status that should show)
    /// goes out again. The send history is kept, so a reconnect can't be used to go over the limit.
    /// </summary>
    public void Reconnected(string? current)
    {
        _shown = null;
        Set(current);
    }

    /// <summary>Drops the waiting update (a clear with no connection to send it on).</summary>
    public void DropPending()
    {
        _shown = _pending;
        _hasPending = false;
    }

    private void Forget(DateTimeOffset now)
    {
        while (_sends.Count > 0 && now - _sends.Peek() >= Window)
        {
            _sends.Dequeue();
        }
    }
}
