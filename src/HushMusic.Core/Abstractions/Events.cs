using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

public sealed class PlaybackStatusChangedEventArgs(PlaybackStatus status) : EventArgs
{
    public PlaybackStatus Status { get; } = status;
}

public sealed class TrackChangedEventArgs(Track? track, QueueItem? item) : EventArgs
{
    public Track? Track { get; } = track;

    public QueueItem? Item { get; } = item;
}

public sealed class PositionChangedEventArgs(TimeSpan position, TimeSpan duration) : EventArgs
{
    public TimeSpan Position { get; } = position;

    public TimeSpan Duration { get; } = duration;
}

public sealed class PlaybackErrorEventArgs(Track? track, string message, Exception? exception) : EventArgs
{
    public Track? Track { get; } = track;

    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}
