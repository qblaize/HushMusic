using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>
/// "Keep playing when the queue ends" (<see cref="AppSettings.AutoplayWhenQueueEnds"/>): when a finite queue (an album, a
/// playlist, a list of songs) reaches its last track with repeat off, songs similar to that track are appended after it and
/// kept topped up like a radio. Radio and mix queues extend themselves and are left alone, as are live radio stations.
/// Implemented in Core.
/// </summary>
public interface IQueueAutoplay
{
    /// <summary>The queue items autoplay added to the current queue, in no particular order. Empty when it added none.</summary>
    IReadOnlySet<Guid> SuggestedItemIds { get; }

    /// <summary>The track the suggestions are based on (the queue's last track when they were fetched).</summary>
    Track? Seed { get; }

    /// <summary>Raised when <see cref="SuggestedItemIds"/> changes, on the thread that changed it.</summary>
    event EventHandler? Changed;
}
