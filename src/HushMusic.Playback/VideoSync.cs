namespace HushMusic.Playback;

/// <summary>What the picture player should do to stay with the sound (see <see cref="VideoSync"/>).</summary>
internal enum VideoSyncAction
{
    /// <summary>Nothing to change.</summary>
    None,

    /// <summary>Pause where it is.</summary>
    Pause,

    /// <summary>Pause and seek to <see cref="VideoSyncDecision.Target"/>.</summary>
    Seek,

    /// <summary>Stay paused (ahead of the sound) and start in <see cref="VideoSyncDecision.Delay"/>.</summary>
    Hold,

    /// <summary>Play at <see cref="VideoSyncDecision.Rate"/>.</summary>
    Play,
}

internal readonly record struct VideoSyncDecision(VideoSyncAction Action, TimeSpan Target = default, TimeSpan Delay = default, double Rate = 1)
{
    public static VideoSyncDecision None => new(VideoSyncAction.None);

    public static VideoSyncDecision Pause => new(VideoSyncAction.Pause);

    public static VideoSyncDecision SeekTo(TimeSpan target) => new(VideoSyncAction.Seek, Target: target < TimeSpan.Zero ? TimeSpan.Zero : target);

    public static VideoSyncDecision HoldFor(TimeSpan delay) => new(VideoSyncAction.Hold, Delay: delay);

    public static VideoSyncDecision PlayAt(double rate) => new(VideoSyncAction.Play, Rate: rate);
}

/// <summary>
/// Keeps a muted picture-only player in step with the audio player, from their two clocks.
/// </summary>
/// <remarks>
/// Seeking a video stream is slow: from a few hundred milliseconds near what is buffered to a few seconds elsewhere,
/// while the sound plays on, and Media Foundation restarts the picture's clock only once the seek is done. So a seek
/// while the sound plays aims ahead of it by the expected seek time (<see cref="NextLead"/>), the picture waits there,
/// paused, and starts as the sound reaches it. Small differences are then evened out by playing the picture slightly
/// faster or slower, which nobody notices, instead of jumping.
/// </remarks>
internal static class VideoSync
{
    /// <summary>Further apart than this while playing: seek.</summary>
    public static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(1);

    /// <summary>A picture this far behind a paused-and-waiting start still catches up by speed instead of seeking again.</summary>
    public static readonly TimeSpan CatchUpLimit = TimeSpan.FromMilliseconds(300);

    /// <summary>With the sound paused, the picture is moved to the exact spot when it is further off than this.</summary>
    public static readonly TimeSpan PausedTolerance = TimeSpan.FromMilliseconds(80);

    /// <summary>Closer than this, the picture plays at normal speed.</summary>
    public static readonly TimeSpan InStep = TimeSpan.FromMilliseconds(40);

    /// <summary>Play shows the next frame about this long after it is called.</summary>
    public static readonly TimeSpan StartLatency = TimeSpan.FromMilliseconds(20);

    /// <summary>Waiting longer than this for the sound means the sound jumped back: seek instead.</summary>
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(4);

    public static readonly TimeSpan InitialLead = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan MinLead = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxLead = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan LeadMargin = TimeSpan.FromMilliseconds(150);

    /// <summary>The most the picture's speed is changed by (12%).</summary>
    public const double MaxRateChange = 0.12;

    /// <param name="audioPlaying">The sound is playing (not paused, loading or buffering).</param>
    /// <param name="audio">The sound's position.</param>
    /// <param name="video">The picture's position.</param>
    /// <param name="videoPlaying">The picture player was told to play.</param>
    /// <param name="lead">How far ahead of the sound a seek aims while the sound plays.</param>
    public static VideoSyncDecision Decide(bool audioPlaying, TimeSpan audio, TimeSpan video, bool videoPlaying, TimeSpan lead)
    {
        var drift = audio - video; // > 0: the picture is behind
        if (!audioPlaying)
        {
            return drift.Duration() > PausedTolerance ? VideoSyncDecision.SeekTo(audio)
                : videoPlaying ? VideoSyncDecision.Pause
                : VideoSyncDecision.None;
        }

        if (videoPlaying)
        {
            return drift.Duration() > SeekThreshold ? VideoSyncDecision.SeekTo(audio + lead) : VideoSyncDecision.PlayAt(RateFor(drift));
        }

        // Paused ahead of the sound (where a seek aimed): start when it gets there.
        if (-drift > StartLatency && -drift <= MaxHold)
        {
            return VideoSyncDecision.HoldFor(-drift - StartLatency);
        }

        return drift <= CatchUpLimit && -drift <= StartLatency
            ? VideoSyncDecision.PlayAt(RateFor(drift))
            : VideoSyncDecision.SeekTo(audio + lead);
    }

    /// <summary>Playback rate that closes <paramref name="drift"/> (sound minus picture) over about two seconds.</summary>
    public static double RateFor(TimeSpan drift) =>
        drift.Duration() < InStep ? 1 : 1 + Math.Clamp(drift.TotalSeconds * 0.5, -MaxRateChange, MaxRateChange);

    /// <summary>The next seek lead, from the last one and how long the last seek took.</summary>
    public static TimeSpan NextLead(TimeSpan current, TimeSpan seekTook)
    {
        var next = (current + seekTook + LeadMargin) / 2;
        return next < MinLead ? MinLead : next > MaxLead ? MaxLead : next;
    }
}
