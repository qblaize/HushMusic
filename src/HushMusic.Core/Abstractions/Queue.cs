using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>A queue entry. <see cref="Id"/> is unique even when the same track is queued twice.</summary>
public sealed record QueueItem(Guid Id, Track Track)
{
    public static QueueItem Create(Track track) => new(Guid.NewGuid(), track);
}

public enum QueueSourceKind
{
    Manual,
    Album,
    Playlist,
    Artist,
    Search,
    Radio,
    UpNext,
    LikedSongs,
    Library,
    History,

    /// <summary>Live internet radio stations (a genre, favourites or directory results).</summary>
    LiveRadio,
}

/// <summary>Where the queue came from, e.g. (Playlist, "PL...", "My mix").</summary>
public sealed record QueueSource(QueueSourceKind Kind, string? Id = null, string? Title = null);

/// <summary>
/// The whole queue at one moment, for saving and restoring a session (<see cref="IQueueService.GetSnapshot"/>,
/// <see cref="IQueueService.Restore"/>). <see cref="UnshuffledItems"/> is the original order while shuffled, null otherwise.
/// </summary>
public sealed record QueueSnapshot(
    IReadOnlyList<QueueItem> Items,
    int CurrentIndex,
    QueueSource? Source = null,
    string? Continuation = null,
    RepeatMode RepeatMode = RepeatMode.Off,
    IReadOnlyList<QueueItem>? UnshuffledItems = null);

public enum QueueChangeKind
{
    Reset,
    Added,
    Removed,
    Moved,
    Shuffled,
    Cleared,
}

public sealed class QueueChangedEventArgs(QueueChangeKind kind) : EventArgs
{
    public QueueChangeKind Kind { get; } = kind;
}

public sealed class QueueCurrentChangedEventArgs(QueueItem? previous, QueueItem? current, int currentIndex) : EventArgs
{
    public QueueItem? Previous { get; } = previous;

    public QueueItem? Current { get; } = current;

    public int CurrentIndex { get; } = currentIndex;
}

/// <summary>
/// The play queue. Pure logic (no audio) so features can read and reshape it freely.
/// Thread-safe; events are raised on the calling thread after the change is applied.
/// Implemented in Core.
/// </summary>
public interface IQueueService
{
    /// <summary>Snapshot of the queue in play order.</summary>
    IReadOnlyList<QueueItem> Items { get; }

    /// <summary>-1 when the queue is empty.</summary>
    int CurrentIndex { get; }

    QueueItem? Current { get; }

    QueueSource? Source { get; }

    /// <summary>Opaque token to extend the queue (radio / up next); null when the queue is finite.</summary>
    string? Continuation { get; }

    bool IsShuffled { get; }

    RepeatMode RepeatMode { get; set; }

    event EventHandler<QueueChangedEventArgs>? Changed;

    event EventHandler<QueueCurrentChangedEventArgs>? CurrentChanged;

    void Load(IReadOnlyList<Track> tracks, int startIndex = 0, QueueSource? source = null, string? continuation = null);

    /// <summary>Adds to the end of the queue.</summary>
    void Enqueue(IReadOnlyList<Track> tracks);

    /// <summary>Inserts right after the current item.</summary>
    void EnqueueNext(IReadOnlyList<Track> tracks);

    /// <summary>Appends tracks fetched with <see cref="Continuation"/> and stores the next token.</summary>
    void AppendContinuation(IReadOnlyList<Track> tracks, string? nextContinuation);

    void RemoveAt(int index);

    void Move(int oldIndex, int newIndex);

    void Clear();

    /// <summary>Shuffles everything after the current item; disabling restores the original order.</summary>
    void SetShuffle(bool enabled);

    bool MoveTo(int index);

    /// <summary>
    /// Advances the cursor honouring <see cref="RepeatMode"/>. With RepeatMode.One an automatic advance
    /// stays on the same item, a user-initiated one moves on. Returns null at the end of the queue.
    /// </summary>
    QueueItem? MoveNext(bool userInitiated);

    QueueItem? MovePrevious();

    /// <summary>The item <see cref="MoveNext"/> would return for an automatic advance, without moving.</summary>
    QueueItem? PeekNext();

    /// <summary>
    /// A consistent copy of the whole queue (items, cursor, source, continuation, repeat, shuffle order).
    /// (The default implementation, for test fakes, is not atomic and has no shuffle order.)
    /// </summary>
    QueueSnapshot GetSnapshot() => new(Items, CurrentIndex, Source, Continuation, RepeatMode);

    /// <summary>
    /// Replaces the queue with a saved one; items keep their ids and the shuffle state comes back with its original order.
    /// Raises <see cref="Changed"/> (Reset) and <see cref="CurrentChanged"/> like <see cref="Load"/>.
    /// (The default implementation, for test fakes, reloads the tracks with new ids and no shuffle.)
    /// </summary>
    void Restore(QueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RepeatMode = snapshot.RepeatMode;
        Load([.. snapshot.Items.Select(i => i.Track)], snapshot.CurrentIndex, snapshot.Source, snapshot.Continuation);
    }
}
