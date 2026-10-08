using HushMusic.Core.Models;

namespace HushMusic.Core.Features.LastFm;

/// <summary>
/// Last.fm's scrobbling rules: a track counts once it has been listened to for half its length or 4 minutes, whichever
/// comes first, and only if it is longer than 30 seconds.
/// </summary>
public static class ScrobbleRules
{
    public static readonly TimeSpan MinimumTrackLength = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan MaximumListenThreshold = TimeSpan.FromMinutes(4);

    /// <summary>False for tracks of 30 s or less. An unknown length is allowed (the 4 minute threshold applies).</summary>
    public static bool IsLongEnough(TimeSpan? duration) =>
        duration is not { } length || length <= TimeSpan.Zero || length > MinimumTrackLength;

    /// <summary>How long the user must actually listen before the track is scrobbled.</summary>
    public static TimeSpan ListenThreshold(TimeSpan? duration) =>
        duration is { } length && length > TimeSpan.Zero
            ? TimeSpan.FromTicks(Math.Min(length.Ticks / 2, MaximumListenThreshold.Ticks))
            : MaximumListenThreshold;

    /// <summary>Null for podcast episodes, live radio and tracks without an artist or title.</summary>
    public static LastFmScrobble? ToScrobble(Track track, DateTimeOffset startedAt, TimeSpan? duration)
    {
        if (track.Type == TrackType.Episode || track.IsLiveRadio || string.IsNullOrWhiteSpace(track.Title))
        {
            return null;
        }

        var artist = track.Artists.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Name))?.Name.Trim();
        if (artist is null)
        {
            return null;
        }

        var album = track.Album?.Name is { } name && !string.IsNullOrWhiteSpace(name) ? name.Trim() : null;
        int? seconds = duration is { } length && length > TimeSpan.Zero ? (int)Math.Round(length.TotalSeconds) : null;
        return new LastFmScrobble(artist, track.Title.Trim(), album, startedAt.ToUnixTimeSeconds(), seconds);
    }
}

/// <summary>
/// One play of one track: counts the time it was actually playing (pauses, buffering and seeks add nothing) and says
/// when it crossed the scrobble threshold. Not thread-safe; the owner locks.
/// </summary>
internal sealed class ListenSession
{
    private readonly TimeProvider _time;
    private TimeSpan _listened;
    private long? _playingSince;

    public ListenSession(Track track, TimeProvider time, bool playing)
    {
        Track = track;
        _time = time;
        StartedAt = time.GetUtcNow();
        Duration = track.Duration is { } length && length > TimeSpan.Zero ? length : null;
        if (playing)
        {
            _playingSince = time.GetTimestamp();
        }
    }

    public Track Track { get; }

    public DateTimeOffset StartedAt { get; }

    /// <summary>From the track metadata, or from the player once it knows.</summary>
    public TimeSpan? Duration { get; private set; }

    public bool IsScrobbled { get; private set; }

    public TimeSpan Listened => _playingSince is { } since ? _listened + _time.GetElapsedTime(since) : _listened;

    public void SetPlaying(bool playing)
    {
        if (playing && _playingSince is null)
        {
            _playingSince = _time.GetTimestamp();
        }
        else if (!playing && _playingSince is { } since)
        {
            _listened += _time.GetElapsedTime(since);
            _playingSince = null;
        }
    }

    public void ReportDuration(TimeSpan duration)
    {
        if (Duration is null && duration > TimeSpan.Zero)
        {
            Duration = duration;
        }
    }

    /// <summary>Returns the scrobble the first time the threshold is crossed, then null.</summary>
    public LastFmScrobble? TryComplete()
    {
        if (IsScrobbled || !ScrobbleRules.IsLongEnough(Duration) || Listened < ScrobbleRules.ListenThreshold(Duration))
        {
            return null;
        }

        IsScrobbled = true;
        return ScrobbleRules.ToScrobble(Track, StartedAt, Duration);
    }
}
