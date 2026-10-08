namespace HushMusic.Playback;

/// <summary>How the player moves from the current track to the next one.</summary>
internal enum TransitionKind
{
    /// <summary>Nothing is prepared: the next item (if any) is resolved and opened when the current one ends.</summary>
    None,

    /// <summary>The next item is opened ahead of time on the spare player and started the moment the current one ends.</summary>
    Gapless,

    /// <summary>The next item is opened ahead of time and started <see cref="TransitionPlan.Fade"/> before the end, blended in.</summary>
    Crossfade,
}

/// <summary>What the player knows about the current and the next item when it plans the transition between them.</summary>
internal readonly record struct TransitionContext
{
    /// <summary>The crossfade setting (zero: off).</summary>
    public TimeSpan Crossfade { get; init; }

    /// <summary>Length of the current track; zero when unknown.</summary>
    public TimeSpan Duration { get; init; }

    public bool HasNext { get; init; }

    /// <summary>The next item is the current one again (repeat one, or a one-item queue on repeat).</summary>
    public bool NextIsCurrent { get; init; }

    public bool RepeatOne { get; init; }

    public bool CurrentIsLive { get; init; }

    public bool NextIsLive { get; init; }

    /// <summary>The sleep timer stops at the end of this track (<see cref="HushMusic.Core.Abstractions.IPlayer.PauseAtEndOfTrack"/>).</summary>
    public bool PauseAtEnd { get; init; }

    /// <summary>The user sought into the last <see cref="Crossfade"/> seconds of the track.</summary>
    public bool SoughtIntoFade { get; init; }

    /// <summary>Length of the next track, when known.</summary>
    public TimeSpan? NextDuration { get; init; }
}

/// <param name="Kind">How the transition happens.</param>
/// <param name="Duration">Length of the current track (zero when unknown).</param>
/// <param name="PreloadAt">Position in the current track from which the next item is opened on the spare player.</param>
/// <param name="FadeAt">Position at which the crossfade starts (<see cref="TransitionKind.Crossfade"/> only).</param>
/// <param name="Fade">Length of the crossfade (<see cref="TransitionKind.Crossfade"/> only).</param>
/// <param name="Reason">Why this kind was chosen, for the log.</param>
internal readonly record struct TransitionPlan(
    TransitionKind Kind,
    TimeSpan Duration,
    TimeSpan PreloadAt,
    TimeSpan FadeAt,
    TimeSpan Fade,
    string Reason);

internal enum TransitionAction
{
    /// <summary>Nothing to do yet.</summary>
    None,

    /// <summary>The fade starts within the next position tick: arm a timer for <see cref="TransitionStep.Delay"/>.</summary>
    ArmTimer,

    /// <summary>Start the crossfade now, over <see cref="TransitionStep.Fade"/>.</summary>
    StartCrossfade,

    /// <summary>The fade is due but the next item isn't open yet: the track plays to its end instead.</summary>
    NotReady,
}

/// <param name="Preload">Open the next item on the spare player now.</param>
/// <param name="Action">What to do about the crossfade.</param>
/// <param name="Delay">For <see cref="TransitionAction.ArmTimer"/>: time until the fade starts.</param>
/// <param name="Fade">For <see cref="TransitionAction.StartCrossfade"/>: how long the fade lasts.</param>
internal readonly record struct TransitionStep(bool Preload, TransitionAction Action, TimeSpan Delay, TimeSpan Fade);

/// <summary>
/// Decides when the next track is prepared and how it takes over: crossfade (setting above zero), gapless (setting at
/// zero, or a crossfade isn't suitable) or not at all (live radio, repeat one, the sleep timer's "end of track").
/// Pure logic; the player feeds it positions and carries out the steps.
/// </summary>
internal static class TransitionPlanner
{
    public static readonly TimeSpan MaxCrossfade = TimeSpan.FromSeconds(12);

    /// <summary>
    /// The next item is opened this long before the fade (or the end). Its link is normally resolved already (prefetched
    /// when the current track started); opening takes well under a second, but a fresh resolve takes 2 – 3 s.
    /// </summary>
    public static readonly TimeSpan PreloadLead = TimeSpan.FromSeconds(25);

    /// <summary>A fade that would be shorter than this (the track is nearly over) is not started.</summary>
    public static readonly TimeSpan MinFade = TimeSpan.FromSeconds(1);

    /// <summary>A fade due within this much is started now rather than on a timer.</summary>
    public static readonly TimeSpan StartTolerance = TimeSpan.FromMilliseconds(20);

    /// <summary>A fade due within this much is started by a one-shot timer (positions are only sampled every 250 ms).</summary>
    public static readonly TimeSpan TimerLookahead = TimeSpan.FromMilliseconds(300);

    /// <summary>The crossfade length for <see cref="HushMusic.Core.Abstractions.AppSettings.CrossfadeSeconds"/>: whole seconds, 0 – 12.</summary>
    public static TimeSpan CrossfadeFromSetting(double seconds) =>
        double.IsFinite(seconds) ? TimeSpan.FromSeconds(Math.Clamp(Math.Round(seconds), 0, MaxCrossfade.TotalSeconds)) : TimeSpan.Zero;

    /// <summary>True when <paramref name="position"/> lies in the last <paramref name="crossfade"/> of the track.</summary>
    public static bool IsInFadeWindow(TimeSpan position, TimeSpan duration, TimeSpan crossfade) =>
        crossfade > TimeSpan.Zero && duration > TimeSpan.Zero && position >= duration - crossfade - StartTolerance;

    public static TransitionPlan Plan(in TransitionContext context)
    {
        var duration = context.Duration > TimeSpan.Zero ? context.Duration : TimeSpan.Zero;
        if (!context.HasNext)
        {
            return None("end of the queue");
        }

        if (context.RepeatOne || context.NextIsCurrent)
        {
            return None("the same item plays again");
        }

        if (context.CurrentIsLive || context.NextIsLive)
        {
            return None("live radio");
        }

        if (context.PauseAtEnd)
        {
            return None("the sleep timer stops at the end of this track");
        }

        var fade = context.Crossfade;
        if (fade <= TimeSpan.Zero)
        {
            return Gapless("crossfade off");
        }

        if (duration == TimeSpan.Zero)
        {
            return Gapless("length unknown");
        }

        if (duration < fade * 2)
        {
            return Gapless("track shorter than twice the crossfade");
        }

        if (context.NextDuration is { } next && next > TimeSpan.Zero && next < fade * 2)
        {
            return Gapless("next track shorter than twice the crossfade");
        }

        if (context.SoughtIntoFade)
        {
            return Gapless("sought into the fade");
        }

        var fadeAt = duration - fade;
        return new TransitionPlan(TransitionKind.Crossfade, duration, Before(fadeAt), fadeAt, fade, "crossfade on");

        TransitionPlan None(string reason) => new(TransitionKind.None, duration, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, reason);

        TransitionPlan Gapless(string reason) => new(TransitionKind.Gapless, duration, Before(duration), TimeSpan.Zero, TimeSpan.Zero, reason);

        static TimeSpan Before(TimeSpan point) => point > PreloadLead ? point - PreloadLead : TimeSpan.Zero;
    }

    /// <param name="plan">The current plan.</param>
    /// <param name="position">Playback position in the current track.</param>
    /// <param name="preloadReady">The next item is open on the spare player.</param>
    /// <param name="preloadPending">The next item is being resolved or opened on the spare player.</param>
    /// <param name="spareFree">The spare player is free (not still fading out the previous track).</param>
    public static TransitionStep Step(in TransitionPlan plan, TimeSpan position, bool preloadReady, bool preloadPending, bool spareFree)
    {
        if (plan.Kind == TransitionKind.None)
        {
            return default;
        }

        var preload = spareFree && !preloadReady && !preloadPending && position >= plan.PreloadAt;
        if (plan.Kind == TransitionKind.Gapless)
        {
            return new TransitionStep(preload, TransitionAction.None, TimeSpan.Zero, TimeSpan.Zero);
        }

        var untilFade = plan.FadeAt - position;
        if (untilFade > StartTolerance)
        {
            return untilFade <= TimerLookahead
                ? new TransitionStep(preload, TransitionAction.ArmTimer, untilFade, TimeSpan.Zero)
                : new TransitionStep(preload, TransitionAction.None, TimeSpan.Zero, TimeSpan.Zero);
        }

        // Due (or late, e.g. the next item only just opened): fade over what is left, so both ends meet the track's end.
        var remaining = plan.Duration - position;
        if (remaining < MinFade)
        {
            return new TransitionStep(preload, TransitionAction.None, TimeSpan.Zero, TimeSpan.Zero);
        }

        return preloadReady
            ? new TransitionStep(false, TransitionAction.StartCrossfade, TimeSpan.Zero, remaining < plan.Fade ? remaining : plan.Fade)
            : new TransitionStep(preload, TransitionAction.NotReady, TimeSpan.Zero, TimeSpan.Zero);
    }
}
