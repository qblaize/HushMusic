using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Queue;

/// <summary>
/// Thread-safe play queue. All state lives behind one lock; events are raised after the lock is
/// released, on the thread that made the change.
/// </summary>
/// <remarks>
/// Semantics worth knowing:
/// <list type="bullet">
/// <item><see cref="Load"/> and <see cref="Clear"/> turn shuffle off (a new queue starts in its own order).</item>
/// <item>Shuffle moves the current item to the top and shuffles every other item after it.
/// Turning it off restores the original order, including items added while shuffled.</item>
/// <item>Out-of-range indices passed to <see cref="RemoveAt"/>, <see cref="Move"/> and <see cref="MoveTo"/>
/// are ignored: callers often hold a snapshot that another thread has already changed.</item>
/// <item><see cref="CurrentIndex"/> is -1 exactly when the queue is empty.</item>
/// </list>
/// </remarks>
public sealed class QueueService : IQueueService
{
    private readonly object _gate = new();
    private readonly List<QueueItem> _items = [];

    // Play order before shuffling; kept in sync with additions/removals while shuffled, null otherwise.
    private List<QueueItem>? _unshuffled;
    private QueueItem[]? _snapshot;
    private int _currentIndex = -1;
    private QueueSource? _source;
    private string? _continuation;
    private RepeatMode _repeatMode;

    public event EventHandler<QueueChangedEventArgs>? Changed;

    public event EventHandler<QueueCurrentChangedEventArgs>? CurrentChanged;

    public IReadOnlyList<QueueItem> Items
    {
        get
        {
            lock (_gate)
            {
                return _snapshot ??= [.. _items];
            }
        }
    }

    public int CurrentIndex
    {
        get
        {
            lock (_gate)
            {
                return _currentIndex;
            }
        }
    }

    public QueueItem? Current
    {
        get
        {
            lock (_gate)
            {
                return CurrentNoLock;
            }
        }
    }

    public QueueSource? Source
    {
        get
        {
            lock (_gate)
            {
                return _source;
            }
        }
    }

    public string? Continuation
    {
        get
        {
            lock (_gate)
            {
                return _continuation;
            }
        }
    }

    public bool IsShuffled
    {
        get
        {
            lock (_gate)
            {
                return _unshuffled is not null;
            }
        }
    }

    public RepeatMode RepeatMode
    {
        get
        {
            lock (_gate)
            {
                return _repeatMode;
            }
        }

        set
        {
            lock (_gate)
            {
                _repeatMode = value;
            }
        }
    }

    private QueueItem? CurrentNoLock => _currentIndex >= 0 ? _items[_currentIndex] : null;

    public void Load(IReadOnlyList<Track> tracks, int startIndex = 0, QueueSource? source = null, string? continuation = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var change = Mutate(() =>
        {
            _items.Clear();
            _items.AddRange(tracks.Select(QueueItem.Create));
            _unshuffled = null;
            _source = source;
            _continuation = continuation;
            _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);
            return QueueChangeKind.Reset;
        });
        Raise(change);
    }

    public QueueSnapshot GetSnapshot()
    {
        lock (_gate)
        {
            return new QueueSnapshot(
                _snapshot ??= [.. _items],
                _currentIndex,
                _source,
                _continuation,
                _repeatMode,
                _unshuffled is null ? null : [.. _unshuffled]);
        }
    }

    public void Restore(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var items = snapshot.Items.Where(i => i?.Track is not null).ToList();

        // Item ids must stay unique; a saved queue that breaks that gets fresh ids (and loses its shuffle order).
        var unique = items.Select(i => i.Id).Distinct().Count() == items.Count;
        if (!unique)
        {
            items = [.. items.Select(i => QueueItem.Create(i.Track))];
        }

        List<QueueItem>? original = null;
        if (unique && snapshot.UnshuffledItems is { } saved && SameItems(saved, items))
        {
            var byId = items.ToDictionary(i => i.Id);
            original = [.. saved.Select(i => byId[i.Id])];
        }

        var change = Mutate(() =>
        {
            _items.Clear();
            _items.AddRange(items);
            _unshuffled = original;
            _source = snapshot.Source;
            _continuation = snapshot.Continuation;
            _repeatMode = snapshot.RepeatMode;
            _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(snapshot.CurrentIndex, 0, _items.Count - 1);
            return QueueChangeKind.Reset;
        });
        Raise(change);
    }

    public void Enqueue(IReadOnlyList<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return;
        }

        var change = Mutate(() =>
        {
            var added = tracks.Select(QueueItem.Create).ToList();
            _items.AddRange(added);
            _unshuffled?.AddRange(added);
            StartIfEmptyNoLock();
            return QueueChangeKind.Added;
        });
        Raise(change);
    }

    public void EnqueueNext(IReadOnlyList<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        if (tracks.Count == 0)
        {
            return;
        }

        var change = Mutate(() =>
        {
            var added = tracks.Select(QueueItem.Create).ToList();
            var current = CurrentNoLock;
            _items.InsertRange(_currentIndex + 1, added);
            if (_unshuffled is not null)
            {
                // Keep "play next" items right after the current one when shuffle is turned off again.
                var at = current is null ? -1 : _unshuffled.FindIndex(i => i.Id == current.Id);
                _unshuffled.InsertRange(at + 1, added);
            }

            StartIfEmptyNoLock();
            return QueueChangeKind.Added;
        });
        Raise(change);
    }

    public void AppendContinuation(IReadOnlyList<Track> tracks, string? nextContinuation)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var change = Mutate(() =>
        {
            _continuation = nextContinuation;
            if (tracks.Count == 0)
            {
                return null;
            }

            var added = tracks.Select(QueueItem.Create).ToList();
            _items.AddRange(added);
            _unshuffled?.AddRange(added);
            StartIfEmptyNoLock();
            return QueueChangeKind.Added;
        });
        Raise(change);
    }

    public void RemoveAt(int index)
    {
        var change = Mutate(() =>
        {
            if (index < 0 || index >= _items.Count)
            {
                return null;
            }

            var removed = _items[index];
            _items.RemoveAt(index);
            _unshuffled?.RemoveAll(i => i.Id == removed.Id);

            if (index < _currentIndex)
            {
                _currentIndex--;
            }
            else if (index == _currentIndex)
            {
                // The following item takes the removed one's place; at the end, fall back to the new last item.
                _currentIndex = Math.Min(index, _items.Count - 1);
            }

            return QueueChangeKind.Removed;
        });
        Raise(change);
    }

    public void Move(int oldIndex, int newIndex)
    {
        var change = Mutate(() =>
        {
            if (oldIndex < 0 || oldIndex >= _items.Count || newIndex < 0 || newIndex >= _items.Count || oldIndex == newIndex)
            {
                return null;
            }

            var item = _items[oldIndex];
            _items.RemoveAt(oldIndex);
            _items.Insert(newIndex, item);

            if (oldIndex == _currentIndex)
            {
                _currentIndex = newIndex;
            }
            else if (oldIndex < _currentIndex && newIndex >= _currentIndex)
            {
                _currentIndex--;
            }
            else if (oldIndex > _currentIndex && newIndex <= _currentIndex)
            {
                _currentIndex++;
            }

            return QueueChangeKind.Moved;
        });
        Raise(change);
    }

    public void Clear()
    {
        var change = Mutate(() =>
        {
            _items.Clear();
            _unshuffled = null;
            _source = null;
            _continuation = null;
            _currentIndex = -1;
            return QueueChangeKind.Cleared;
        });
        Raise(change);
    }

    public void SetShuffle(bool enabled)
    {
        var change = Mutate(() =>
        {
            if (enabled == _unshuffled is not null)
            {
                return null;
            }

            var current = CurrentNoLock;
            if (enabled)
            {
                _unshuffled = [.. _items];
                var rest = _items.Where(i => i.Id != current?.Id).ToArray();
                Random.Shared.Shuffle(rest);
                _items.Clear();
                if (current is not null)
                {
                    _items.Add(current);
                }

                _items.AddRange(rest);
                _currentIndex = current is null ? -1 : 0;
            }
            else
            {
                _items.Clear();
                _items.AddRange(_unshuffled!);
                _unshuffled = null;
                _currentIndex = current is null ? -1 : _items.FindIndex(i => i.Id == current.Id);
            }

            return QueueChangeKind.Shuffled;
        });
        Raise(change);
    }

    public bool MoveTo(int index)
    {
        var moved = false;
        var change = Mutate(() =>
        {
            if (index < 0 || index >= _items.Count)
            {
                return null;
            }

            moved = true;
            _currentIndex = index;
            return null;
        });
        Raise(change);
        return moved;
    }

    public QueueItem? MoveNext(bool userInitiated)
    {
        QueueItem? next = null;
        var change = Mutate(() =>
        {
            var target = NextIndexNoLock(userInitiated);
            if (target >= 0)
            {
                _currentIndex = target;
                next = _items[target];
            }

            return null;
        });
        Raise(change);
        return next;
    }

    public QueueItem? MovePrevious()
    {
        QueueItem? previous = null;
        var change = Mutate(() =>
        {
            if (_items.Count == 0)
            {
                return null;
            }

            int target;
            if (_currentIndex > 0)
            {
                target = _currentIndex - 1;
            }
            else if (_repeatMode != RepeatMode.Off)
            {
                target = _items.Count - 1;
            }
            else
            {
                return null;
            }

            _currentIndex = target;
            previous = _items[target];
            return null;
        });
        Raise(change);
        return previous;
    }

    public QueueItem? PeekNext()
    {
        lock (_gate)
        {
            var target = NextIndexNoLock(userInitiated: false);
            return target >= 0 ? _items[target] : null;
        }
    }

    // Returns -1 when there is nothing to advance to.
    private int NextIndexNoLock(bool userInitiated)
    {
        if (_items.Count == 0)
        {
            return -1;
        }

        if (_repeatMode == RepeatMode.One && !userInitiated)
        {
            return _currentIndex;
        }

        if (_currentIndex + 1 < _items.Count)
        {
            return _currentIndex + 1;
        }

        // At the end: any repeat mode wraps around (a user "next" with RepeatMode.One behaves like All).
        return _repeatMode == RepeatMode.Off ? -1 : 0;
    }

    private static bool SameItems(IReadOnlyList<QueueItem> a, IReadOnlyList<QueueItem> b) =>
        a.Count == b.Count && a.Select(i => i?.Id).ToHashSet().SetEquals(b.Select(i => (Guid?)i.Id));

    private void StartIfEmptyNoLock()
    {
        if (_currentIndex < 0 && _items.Count > 0)
        {
            _currentIndex = 0;
        }
    }

    /// <summary>
    /// Runs <paramref name="mutation"/> under the lock and describes which events to raise afterwards.
    /// The mutation returns the <see cref="QueueChangeKind"/> to report, or null when the list itself did not change.
    /// </summary>
    private PendingChange Mutate(Func<QueueChangeKind?> mutation)
    {
        lock (_gate)
        {
            var previous = CurrentNoLock;
            var previousIndex = _currentIndex;
            var kind = mutation();
            if (kind is not null)
            {
                _snapshot = null;
            }

            var current = CurrentNoLock;
            var currentChanged = previous?.Id != current?.Id || previousIndex != _currentIndex;
            return new PendingChange(kind, currentChanged, previous, current, _currentIndex);
        }
    }

    private void Raise(PendingChange change)
    {
        if (change.Kind is { } kind)
        {
            Changed?.Invoke(this, new QueueChangedEventArgs(kind));
        }

        if (change.CurrentChanged)
        {
            CurrentChanged?.Invoke(this, new QueueCurrentChangedEventArgs(change.Previous, change.Current, change.CurrentIndex));
        }
    }

    private readonly record struct PendingChange(
        QueueChangeKind? Kind,
        bool CurrentChanged,
        QueueItem? Previous,
        QueueItem? Current,
        int CurrentIndex);
}
