using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class TransitionPlannerTests
{
    private static readonly TimeSpan Song = TimeSpan.FromMinutes(3);

    private static TransitionContext Context(double crossfadeSeconds = 6, TimeSpan? duration = null) => new()
    {
        Crossfade = TimeSpan.FromSeconds(crossfadeSeconds),
        Duration = duration ?? Song,
        HasNext = true,
        NextDuration = Song,
    };

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5.6, 6)]
    [InlineData(12, 12)]
    [InlineData(30, 12)]
    [InlineData(-3, 0)]
    [InlineData(double.NaN, 0)]
    public void The_setting_is_whole_seconds_from_0_to_12(double setting, double expected) =>
        Assert.Equal(S(expected), TransitionPlanner.CrossfadeFromSetting(setting));

    [Fact]
    public void Crossfade_starts_its_length_before_the_end_and_preloads_25_s_earlier()
    {
        var plan = TransitionPlanner.Plan(Context());

        Assert.Equal(TransitionKind.Crossfade, plan.Kind);
        Assert.Equal(S(174), plan.FadeAt);
        Assert.Equal(S(6), plan.Fade);
        Assert.Equal(S(149), plan.PreloadAt);
        Assert.Equal(Song, plan.Duration);
    }

    [Fact]
    public void Off_means_gapless_with_the_next_item_opened_25_s_before_the_end()
    {
        var plan = TransitionPlanner.Plan(Context(crossfadeSeconds: 0));

        Assert.Equal(TransitionKind.Gapless, plan.Kind);
        Assert.Equal(S(155), plan.PreloadAt);
        Assert.Equal(TimeSpan.Zero, plan.Fade);
    }

    [Fact]
    public void A_short_track_preloads_from_the_start()
    {
        var plan = TransitionPlanner.Plan(Context(crossfadeSeconds: 6, duration: S(20)));

        Assert.Equal(TransitionKind.Crossfade, plan.Kind);
        Assert.Equal(S(14), plan.FadeAt);
        Assert.Equal(TimeSpan.Zero, plan.PreloadAt);
    }

    [Fact]
    public void Nothing_is_prepared_at_the_end_of_the_queue_or_for_the_same_item_again()
    {
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context() with { HasNext = false }).Kind);
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context() with { NextIsCurrent = true }).Kind);
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context() with { RepeatOne = true }).Kind);
    }

    [Fact]
    public void Live_radio_is_never_preloaded_or_crossfaded_in_either_direction()
    {
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context() with { CurrentIsLive = true }).Kind);
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context() with { NextIsLive = true }).Kind);
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context(crossfadeSeconds: 0) with { NextIsLive = true }).Kind);
    }

    [Fact]
    public void The_sleep_timers_end_of_track_pause_wins()
    {
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context() with { PauseAtEnd = true }).Kind);
        Assert.Equal(TransitionKind.None, TransitionPlanner.Plan(Context(crossfadeSeconds: 0) with { PauseAtEnd = true }).Kind);
    }

    [Theory]
    [InlineData(11.9, 180, "track shorter than twice the crossfade")]
    [InlineData(180, 11.9, "next track shorter than twice the crossfade")]
    public void Tracks_shorter_than_twice_the_crossfade_go_gapless(double current, double next, string reason)
    {
        var plan = TransitionPlanner.Plan(Context(crossfadeSeconds: 6, duration: S(current)) with { NextDuration = S(next) });

        Assert.Equal(TransitionKind.Gapless, plan.Kind);
        Assert.Equal(reason, plan.Reason);
    }

    [Fact]
    public void Exactly_twice_the_crossfade_still_fades()
    {
        var plan = TransitionPlanner.Plan(Context(crossfadeSeconds: 6, duration: S(12)) with { NextDuration = S(12) });

        Assert.Equal(TransitionKind.Crossfade, plan.Kind);
    }

    [Fact]
    public void An_unknown_length_goes_gapless_and_an_unknown_next_length_still_fades()
    {
        Assert.Equal(TransitionKind.Gapless, TransitionPlanner.Plan(Context(duration: TimeSpan.Zero)).Kind);
        Assert.Equal(TransitionKind.Crossfade, TransitionPlanner.Plan(Context() with { NextDuration = null }).Kind);
    }

    [Fact]
    public void Seeking_into_the_fade_window_plays_the_end_and_goes_gapless()
    {
        Assert.True(TransitionPlanner.IsInFadeWindow(S(175), Song, S(6)));
        Assert.True(TransitionPlanner.IsInFadeWindow(S(174), Song, S(6)));
        Assert.False(TransitionPlanner.IsInFadeWindow(S(173), Song, S(6)));
        Assert.False(TransitionPlanner.IsInFadeWindow(S(179), Song, TimeSpan.Zero));
        Assert.False(TransitionPlanner.IsInFadeWindow(S(179), TimeSpan.Zero, S(6)));

        var plan = TransitionPlanner.Plan(Context() with { SoughtIntoFade = true });
        Assert.Equal(TransitionKind.Gapless, plan.Kind);
    }

    [Fact]
    public void Step_does_nothing_without_a_plan()
    {
        var plan = TransitionPlanner.Plan(Context() with { HasNext = false });

        Assert.Equal(default, TransitionPlanner.Step(plan, S(179), preloadReady: false, preloadPending: false, spareFree: true));
    }

    [Fact]
    public void Step_preloads_once_the_preload_point_is_reached_and_the_spare_is_free()
    {
        var plan = TransitionPlanner.Plan(Context());

        Assert.False(TransitionPlanner.Step(plan, S(148.9), false, false, true).Preload);
        Assert.True(TransitionPlanner.Step(plan, S(149), false, false, true).Preload);
        Assert.False(TransitionPlanner.Step(plan, S(150), false, preloadPending: true, true).Preload);
        Assert.False(TransitionPlanner.Step(plan, S(150), preloadReady: true, false, true).Preload);
        Assert.False(TransitionPlanner.Step(plan, S(150), false, false, spareFree: false).Preload);
        Assert.Equal(TransitionAction.None, TransitionPlanner.Step(plan, S(150), false, false, true).Action);
    }

    [Fact]
    public void Gapless_steps_only_ever_preload()
    {
        var plan = TransitionPlanner.Plan(Context(crossfadeSeconds: 0));

        var step = TransitionPlanner.Step(plan, S(179.9), false, false, true);

        Assert.True(step.Preload);
        Assert.Equal(TransitionAction.None, step.Action);
    }

    [Fact]
    public void Step_arms_a_timer_when_the_fade_is_due_before_the_next_position_tick()
    {
        var plan = TransitionPlanner.Plan(Context());

        Assert.Equal(TransitionAction.None, TransitionPlanner.Step(plan, S(173.6), true, false, true).Action);

        var step = TransitionPlanner.Step(plan, S(173.8), true, false, true);
        Assert.Equal(TransitionAction.ArmTimer, step.Action);
        Assert.Equal(200, step.Delay.TotalMilliseconds, 3);
    }

    [Fact]
    public void Step_starts_the_full_fade_on_time()
    {
        var plan = TransitionPlanner.Plan(Context());

        var early = TransitionPlanner.Step(plan, S(173.99), true, false, true);
        Assert.Equal(TransitionAction.StartCrossfade, early.Action);
        Assert.Equal(S(6), early.Fade);
        Assert.False(early.Preload);

        Assert.Equal(S(6), TransitionPlanner.Step(plan, S(174), true, false, true).Fade);
    }

    [Fact]
    public void A_late_fade_covers_what_is_left_so_it_still_ends_with_the_track()
    {
        var plan = TransitionPlanner.Plan(Context());

        var step = TransitionPlanner.Step(plan, S(176.5), true, false, true);

        Assert.Equal(TransitionAction.StartCrossfade, step.Action);
        Assert.Equal(S(3.5), step.Fade);
    }

    [Fact]
    public void Too_close_to_the_end_no_fade_starts()
    {
        var plan = TransitionPlanner.Plan(Context());

        Assert.Equal(TransitionAction.None, TransitionPlanner.Step(plan, S(179.2), true, false, true).Action);
    }

    [Fact]
    public void A_due_fade_without_the_next_item_open_is_reported_and_still_preloads_when_it_can()
    {
        var plan = TransitionPlanner.Plan(Context());

        var notStarted = TransitionPlanner.Step(plan, S(175), false, false, true);
        Assert.Equal(TransitionAction.NotReady, notStarted.Action);
        Assert.True(notStarted.Preload);

        var opening = TransitionPlanner.Step(plan, S(175), false, preloadPending: true, true);
        Assert.Equal(TransitionAction.NotReady, opening.Action);
        Assert.False(opening.Preload);
    }
}
