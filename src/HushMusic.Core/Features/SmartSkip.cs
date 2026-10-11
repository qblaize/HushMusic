using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>When moving on from a song counts as a quick skip, and when it counts as listening to it. Pure.</summary>
public static class SkipRules
{
    /// <summary>Quick skips, with no listen in between, that make a song "often skipped".</summary>
    public const int OftenSkippedAfter = 3;

    /// <summary>A skip in the first 30 seconds is quick.</summary>
    public static readonly TimeSpan QuickSkipWindow = TimeSpan.FromSeconds(30);

    /// <summary>Moving on before this is a quick skip: 30 seconds, or a quarter of the song when that is shorter.</summary>
    public static TimeSpan QuickSkipLimit(TimeSpan? duration) =>
        duration is { } length && length > TimeSpan.Zero && length / 4 < QuickSkipWindow ? length / 4 : QuickSkipWindow;

    public static bool IsQuickSkip(TimeSpan heard, TimeSpan? duration) => heard < QuickSkipLimit(duration);

    /// <summary>Hearing at least half of the song clears its skips. A song of unknown length only by playing to its end.</summary>
    public static bool IsListen(TimeSpan heard, TimeSpan? duration) =>
        duration is { } length && length > TimeSpan.Zero && heard >= length / 2;
}

public enum SkipVerdict
{
    None,

    /// <summary>The user moved on within the first seconds.</summary>
    QuickSkip,

    /// <summary>The user heard at least half of it, or it played to the end.</summary>
    Listened,
}

/// <summary>What a play turned out to be. <see cref="Track"/> is null with <see cref="SkipVerdict.None"/>.</summary>
public readonly record struct SkipOutcome(SkipVerdict Verdict, Track? Track)
{
    public static SkipOutcome None => default;
}

/// <summary>
/// Follows the current play and tells, when it ends, whether it was a quick skip or a listen. A play begins when its audio
/// starts (a song skipped while it was still loading was never heard). How far it got is where it started (a restored
/// session resumes part-way) plus the time spent actually playing; pauses and buffering don't count. Times come from a
/// monotonic clock. Not thread-safe.
/// </summary>
/// <remarks>
/// Moving on counts as a skip only when it is forwards to a different song: Next, or picking another song (also by
/// replacing the queue). Going back to an earlier song, playing the same song again (e.g. "Start radio" on it), stopping,
/// a failed stream and live radio, on either side, never count.
/// </remarks>
public sealed class SkipTracker
{
    private Play? _play;
    private bool _isPlaying;

    /// <summary>The song of the current play, or null.</summary>
    public string? CurrentVideoId => _play?.Track.VideoId;

    /// <summary>The queue item of the current play, when known.</summary>
    public Guid? CurrentItemId => _play?.ItemId;

    /// <summary>Audio started for <paramref name="track"/>. Ends the previous play (without a skip: the queue didn't move).</summary>
    /// <param name="position">Where playback started.</param>
    public SkipOutcome Started(Guid? itemId, Track track, TimeSpan position, TimeSpan? duration, TimeSpan now)
    {
        ArgumentNullException.ThrowIfNull(track);
        var outcome = End(now, movedOn: false);
        _isPlaying = true;
        _play = track.IsLiveRadio ? null : new Play(itemId, track, position, Positive(duration) ?? Positive(track.Duration), now);
        return outcome;
    }

    /// <summary>The player started or stopped producing sound. Hearing half of the song counts as a listen right away.</summary>
    public SkipOutcome PlayingChanged(bool isPlaying, TimeSpan now)
    {
        if (_play is { } play && _isPlaying)
        {
            play.Heard += Elapsed(play.Since, now);
        }

        _isPlaying = isPlaying;
        if (_play is not { } current)
        {
            return SkipOutcome.None;
        }

        current.Since = now;
        if (!current.Listened && SkipRules.IsListen(current.Heard, current.Duration))
        {
            current.Listened = true;
            return new SkipOutcome(SkipVerdict.Listened, current.Track);
        }

        return SkipOutcome.None;
    }

    /// <summary>The length of the playing song became known (the track didn't carry it).</summary>
    public void DurationKnown(string videoId, TimeSpan duration)
    {
        if (_play is { Duration: null } play && play.Track.VideoId == videoId && duration > TimeSpan.Zero)
        {
            play.Duration = duration;
        }
    }

    /// <summary>The song played to its end (or into the crossfade with the next one).</summary>
    public SkipOutcome Completed(Guid? itemId, Track? track)
    {
        if (_play is not { } play || !IsSame(play, itemId, track))
        {
            return SkipOutcome.None;
        }

        _play = null;
        return play.Listened ? SkipOutcome.None : new SkipOutcome(SkipVerdict.Listened, play.Track);
    }

    /// <summary>The song couldn't be played: whatever happened, it wasn't the user skipping it.</summary>
    public void Failed(Track? track)
    {
        if (_play is { } play && (track is null || track.VideoId == play.Track.VideoId))
        {
            _play = null;
        }
    }

    /// <summary>
    /// Another item became current (or none, <paramref name="track"/> null). <paramref name="wentBack"/>: it comes before
    /// the current one in the queue (Previous, or an earlier song picked from the queue).
    /// </summary>
    public SkipOutcome MovedOn(Guid? itemId, Track? track, bool wentBack, TimeSpan now)
    {
        if (_play is not { } play || (itemId is not null && itemId == play.ItemId))
        {
            return SkipOutcome.None;
        }

        var forwards = track is { IsLiveRadio: false } && track.VideoId != play.Track.VideoId && !wentBack;
        return End(now, forwards);
    }

    private SkipOutcome End(TimeSpan now, bool movedOn)
    {
        if (_play is not { } play)
        {
            return SkipOutcome.None;
        }

        _play = null;
        var heard = play.Heard + (_isPlaying ? Elapsed(play.Since, now) : TimeSpan.Zero);
        if (play.Listened)
        {
            return SkipOutcome.None;
        }

        if (SkipRules.IsListen(heard, play.Duration))
        {
            return new SkipOutcome(SkipVerdict.Listened, play.Track);
        }

        return movedOn && SkipRules.IsQuickSkip(heard, play.Duration) ? new SkipOutcome(SkipVerdict.QuickSkip, play.Track) : SkipOutcome.None;
    }

    // The same queue item when both sides know it; otherwise the same song.
    private static bool IsSame(Play play, Guid? itemId, Track? track) =>
        itemId is not null && play.ItemId is { } id ? itemId == id : track is not null && track.VideoId == play.Track.VideoId;

    private static TimeSpan Elapsed(TimeSpan since, TimeSpan now) => now > since ? now - since : TimeSpan.Zero;

    private static TimeSpan? Positive(TimeSpan? value) => value > TimeSpan.Zero ? value : null;

    private sealed class Play(Guid? itemId, Track track, TimeSpan startedAt, TimeSpan? duration, TimeSpan now)
    {
        public Guid? ItemId { get; } = itemId;

        public Track Track { get; } = track;

        public TimeSpan? Duration { get; set; } = duration;

        /// <summary>Where it started plus the time played since.</summary>
        public TimeSpan Heard { get; set; } = startedAt > TimeSpan.Zero ? startedAt : TimeSpan.Zero;

        /// <summary>When the current stretch of playing began (while playing).</summary>
        public TimeSpan Since { get; set; } = now;

        /// <summary>Already reported as a listen.</summary>
        public bool Listened { get; set; }
    }
}

/// <summary>The skip counts behind <see cref="ISmartSkip"/>. Pure: no I/O, no clock. Not thread-safe.</summary>
public sealed class SkipCounts
{
    /// <summary>Songs kept at most; past that, the longest-ago skipped songs that aren't often skipped are dropped.</summary>
    public const int MaxSongs = 1000;

    private readonly Dictionary<string, SkippedSong> _songs = new(StringComparer.Ordinal);

    public int Count => _songs.Count;

    /// <summary>Every song with skips, often skipped or not.</summary>
    public IEnumerable<SkippedSong> All => _songs.Values;

    public static bool IsOftenSkipped(SkippedSong song) => song.Skips >= SkipRules.OftenSkippedAfter;

    /// <summary>Adds songs read from disk; a song already counted (skipped before the file was read) gets both counts.</summary>
    public void Merge(IEnumerable<SkippedSong> saved)
    {
        foreach (var song in saved)
        {
            if (string.IsNullOrWhiteSpace(song.VideoId) || song.Skips <= 0)
            {
                continue;
            }

            _songs[song.VideoId] = _songs.TryGetValue(song.VideoId, out var known)
                ? known with { Skips = known.Skips + song.Skips, LastSkipped = Max(known.LastSkipped, song.LastSkipped) }
                : song;
        }

        Trim();
    }

    /// <summary>Counts a quick skip. Returns true when the often-skipped list changed.</summary>
    public bool AddSkip(Track track, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(track);
        var skips = _songs.TryGetValue(track.VideoId, out var known) ? known.Skips + 1 : 1;
        _songs[track.VideoId] = new SkippedSong(track.VideoId, track.Title, track.ArtistsText, track.ThumbnailFor(120)?.Url, skips, at);
        Trim();
        return skips >= SkipRules.OftenSkippedAfter;
    }

    /// <summary>Forgets the song's skips (a listen, or the user forgave it). Returns true when it was often skipped.</summary>
    public bool Clear(string videoId) => _songs.Remove(videoId, out var song) && IsOftenSkipped(song);

    public bool Contains(string videoId) => _songs.ContainsKey(videoId);

    /// <summary>The often-skipped songs, most recently skipped first.</summary>
    public List<SkippedSong> OftenSkipped() =>
        [.. _songs.Values.Where(IsOftenSkipped).OrderByDescending(s => s.LastSkipped).ThenBy(s => s.VideoId, StringComparer.Ordinal)];

    private void Trim()
    {
        var excess = _songs.Count - MaxSongs;
        if (excess <= 0)
        {
            return;
        }

        foreach (var song in _songs.Values.Where(s => !IsOftenSkipped(s)).OrderBy(s => s.LastSkipped).Take(excess).ToList())
        {
            _songs.Remove(song.VideoId);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a >= b ? a : b;
}
