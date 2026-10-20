using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Services;

/// <summary>One toast in a <see cref="NotificationStack"/>.</summary>
public sealed class StackedNotification
{
    internal StackedNotification(AppNotification notification) => Notification = notification;

    /// <summary>The latest notification merged into this toast.</summary>
    public AppNotification Notification { get; internal set; }

    /// <summary>How many times the same message came in (shown as "×2" from the second one).</summary>
    public int Count { get; internal set; } = 1;

    /// <summary>Errors and toasts with a button stay until they are closed.</summary>
    public bool IsSticky => Notification.Severity == NotificationSeverity.Error || HasButtons(Notification);

    /// <summary>When it goes by itself; null for sticky toasts and while the stack is held.</summary>
    public TimeSpan? DueAt { get; internal set; }

    /// <summary>On screen (one of the newest <see cref="NotificationStack.MaxShown"/>), not waiting.</summary>
    public bool IsShown { get; internal set; }

    // When it would go on its own, before the toasts on screen are put in order (DueAt).
    internal TimeSpan? OwnDueAt { get; set; }

    // Time left while the stack is held.
    internal TimeSpan Left { get; set; }

    internal bool WasShown { get; set; }

    internal static bool HasButtons(AppNotification notification) => notification.Action is not null || notification.SecondaryAction is not null;
}

/// <summary>
/// The in-app toasts and when each one goes, as pure logic: the shell shows <see cref="Shown"/> and calls
/// <see cref="RemoveDue"/> at <see cref="NextDue"/>. The rules keep the stack calm:
/// <list type="bullet">
/// <item>Toasts on screen leave in the order they came, at least <see cref="Gap"/> apart: a newer toast never goes
/// before an older one, so they peel off the top of the stack and nothing under them has to move.</item>
/// <item>Errors and toasts with a button stay until they are closed (<see cref="StackedNotification.IsSticky"/>).</item>
/// <item>The same message again (severity, title and text, without buttons) doesn't add a card: the one there counts up
/// and starts its time over.</item>
/// <item>At most <see cref="MaxShown"/> are on screen, the newest ones. Older ones wait and come back, at the top, when
/// there is room; one that runs out of time while waiting is dropped, a sticky one isn't.</item>
/// <item>While the stack is held (the pointer rests on it) nothing goes; afterwards each toast has at least
/// <see cref="MinimumAfterHold"/> left.</item>
/// </list>
/// Times are monotonic (time since some start) and passed in by the caller.
/// </summary>
public sealed class NotificationStack
{
    public const int MaxShown = 3;

    /// <summary>
    /// Waiting toasts kept at most. Past it the oldest one that would go by itself is dropped first (every notification
    /// is also logged by the notification service).
    /// </summary>
    public const int MaxWaiting = 20;

    /// <summary>How long a toast that goes by itself stays.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(6);

    /// <summary>The least time between two toasts on screen going, so they leave one at a time.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromMilliseconds(400);

    /// <summary>The least time a toast has left after the stack is let go, or when it comes back from waiting.</summary>
    public static readonly TimeSpan MinimumAfterHold = TimeSpan.FromSeconds(2);

    // Oldest first; the last MaxShown are on screen.
    private readonly List<StackedNotification> _items = [];

    /// <summary>On screen, oldest (top) first.</summary>
    public IReadOnlyList<StackedNotification> Shown => _items.Count <= MaxShown ? _items : _items.GetRange(_items.Count - MaxShown, MaxShown);

    public int WaitingCount => Math.Max(0, _items.Count - MaxShown);

    public int Count => _items.Count;

    public bool IsHeld { get; private set; }

    /// <summary>When the next toast goes by itself, or null when none will (all sticky, or held).</summary>
    public TimeSpan? NextDue
    {
        get
        {
            TimeSpan? next = null;
            foreach (var item in _items)
            {
                if (item.DueAt is { } due && (next is null || due < next))
                {
                    next = due;
                }
            }

            return next;
        }
    }

    /// <summary>Adds a notification, or merges it into the same message already there. Returns the toast that shows it.</summary>
    public StackedNotification Add(AppNotification notification, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var item = FindSame(notification);
        if (item is null)
        {
            item = new StackedNotification(notification);
            _items.Add(item);
        }
        else
        {
            item.Count++;
            item.Notification = notification;

            // A waiting one comes back as the newest; one on screen keeps its place.
            if (!item.IsShown)
            {
                _items.Remove(item);
                _items.Add(item);
            }
        }

        StartOver(item, now);
        Update(now);
        return item;
    }

    /// <summary>Closes a toast (its close button, or its action ran). Returns false when it was already gone.</summary>
    public bool Remove(StackedNotification item, TimeSpan now)
    {
        if (!_items.Remove(item))
        {
            return false;
        }

        Update(now);
        return true;
    }

    /// <summary>Removes every toast whose time is up. Returns true when any went.</summary>
    public bool RemoveDue(TimeSpan now)
    {
        if (_items.RemoveAll(item => item.DueAt <= now) == 0)
        {
            return false;
        }

        Update(now);
        return true;
    }

    /// <summary>Stops every toast's time (the pointer rests on the stack).</summary>
    public void Hold(TimeSpan now)
    {
        if (IsHeld)
        {
            return;
        }

        IsHeld = true;
        foreach (var item in _items)
        {
            if (item.DueAt is { } due)
            {
                item.Left = due > now ? due - now : TimeSpan.Zero;
            }

            item.OwnDueAt = null;
        }

        KeepOrder();
    }

    /// <summary>Starts the time again, with at least <see cref="MinimumAfterHold"/> left for each toast.</summary>
    public void Release(TimeSpan now)
    {
        if (!IsHeld)
        {
            return;
        }

        IsHeld = false;
        foreach (var item in _items)
        {
            if (!item.IsSticky)
            {
                item.OwnDueAt = now + Max(item.Left, MinimumAfterHold);
            }
        }

        KeepOrder();
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private static bool IsSame(AppNotification a, AppNotification b) =>
        a.Severity == b.Severity
        && string.Equals(a.Title, b.Title, StringComparison.Ordinal)
        && string.Equals(a.Message, b.Message, StringComparison.Ordinal);

    // Toasts with buttons never merge: each button acts on its own thing (an undo of one removal, say).
    private StackedNotification? FindSame(AppNotification notification)
    {
        if (StackedNotification.HasButtons(notification))
        {
            return null;
        }

        for (var i = _items.Count - 1; i >= 0; i--)
        {
            var item = _items[i];
            if (!StackedNotification.HasButtons(item.Notification) && IsSame(item.Notification, notification))
            {
                return item;
            }
        }

        return null;
    }

    private void StartOver(StackedNotification item, TimeSpan now)
    {
        if (item.IsSticky)
        {
            item.OwnDueAt = null;
        }
        else if (IsHeld)
        {
            item.Left = Lifetime;
        }
        else
        {
            item.OwnDueAt = now + Lifetime;
        }
    }

    // Drops waiting toasts over the limit (the oldest that would go by itself, a sticky one only when all are sticky),
    // marks which toasts are on screen, gives one that comes back from waiting a fair time, then puts the times in order.
    private void Update(TimeSpan now)
    {
        while (_items.Count - MaxShown > MaxWaiting)
        {
            var drop = _items.FindIndex(0, _items.Count - MaxShown, item => !item.IsSticky);
            _items.RemoveAt(drop >= 0 ? drop : 0);
        }

        var firstShown = _items.Count - MaxShown;
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var shown = i >= firstShown;
            if (shown && !item.IsShown && item.WasShown && !item.IsSticky)
            {
                if (IsHeld)
                {
                    item.Left = Max(item.Left, MinimumAfterHold);
                }
                else if (item.OwnDueAt is { } due)
                {
                    item.OwnDueAt = Max(due, now + MinimumAfterHold);
                }
            }

            item.IsShown = shown;
            item.WasShown |= shown;
        }

        KeepOrder();
    }

    // On screen, a newer toast goes at least Gap after the one above it. Waiting toasts keep their own time: they are
    // older and out of sight (so a burst doesn't push the newest ones further and further out), and get a fair time of
    // their own when they come back.
    private void KeepOrder()
    {
        TimeSpan? previous = null;
        foreach (var item in _items)
        {
            if (item.OwnDueAt is not { } due || !item.IsShown)
            {
                item.DueAt = item.OwnDueAt;
                continue;
            }

            if (previous is { } earlier && due < earlier + Gap)
            {
                due = earlier + Gap;
            }

            item.DueAt = due;
            previous = due;
        }
    }
}
