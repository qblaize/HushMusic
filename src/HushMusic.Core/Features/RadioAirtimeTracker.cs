using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// How long the song on air has actually been heard: time counts only while its station is the one playing (not paused,
/// buffering, or another station or song). A new title starts over; the same title again (new artwork) does not. Reports
/// each song once, when it reaches the threshold. Not thread-safe; the caller serializes access.
/// </summary>
public sealed class RadioAirtimeTracker(TimeSpan threshold)
{
    private string? _playingStationId;
    private RadioNowPlaying? _song;
    private TimeSpan _heard;
    private DateTimeOffset? _since;
    private bool _reported;

    /// <summary>The song on air that is being timed, if any.</summary>
    public RadioNowPlaying? Song => _song;

    private bool IsHearing => _song is not null && _song.StationId == _playingStationId;

    /// <summary>The station that is current and playing right now, or null (paused, stopped, a song from YouTube Music).</summary>
    public void SetPlaying(string? stationId, DateTimeOffset now)
    {
        Fold(now);
        _playingStationId = stationId;

        // Another station started: the title it had belongs to the old one.
        if (_song is not null && stationId is not null && _song.StationId != stationId)
        {
            Reset(null);
        }

        Resume(now);
    }

    /// <summary>The station's song on air changed (null: unknown, an ident or an ad).</summary>
    public void SetSong(string stationId, RadioNowPlaying? song, DateTimeOffset now)
    {
        if (song is null)
        {
            if (_song?.StationId == stationId)
            {
                Reset(null);
            }

            return;
        }

        if (_song is not null && _song.StationId == song.StationId && string.Equals(_song.StreamTitle, song.StreamTitle, StringComparison.Ordinal))
        {
            _song = song;
            return;
        }

        Reset(song);
        Resume(now);
    }

    /// <summary>The song, once, when it has been heard for the threshold; otherwise null.</summary>
    public RadioNowPlaying? TakeDue(DateTimeOffset now)
    {
        if (_song is null || _reported || HeardFor(now) < threshold)
        {
            return null;
        }

        _reported = true;
        return _song;
    }

    /// <summary>How long until <see cref="TakeDue"/> reports the song if nothing changes; null when it never will.</summary>
    public TimeSpan? DueIn(DateTimeOffset now)
    {
        if (_song is null || _reported || !IsHearing)
        {
            return null;
        }

        var left = threshold - HeardFor(now);
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private TimeSpan HeardFor(DateTimeOffset now) => _heard + (_since is { } since && now > since ? now - since : TimeSpan.Zero);

    private void Fold(DateTimeOffset now)
    {
        _heard = HeardFor(now);
        _since = null;
    }

    private void Resume(DateTimeOffset now)
    {
        if (IsHearing && _since is null)
        {
            _since = now;
        }
    }

    private void Reset(RadioNowPlaying? song)
    {
        _song = song;
        _heard = TimeSpan.Zero;
        _since = null;
        _reported = false;
    }
}
