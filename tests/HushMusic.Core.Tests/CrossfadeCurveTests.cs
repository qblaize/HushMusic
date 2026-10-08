using HushMusic.Core.Features;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class CrossfadeCurveTests
{
    [Theory]
    [InlineData(0.0)]
    [InlineData(0.1)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1.0)]
    public void Equal_power_halves_keep_the_combined_power_constant(double progress)
    {
        var fadingIn = PlaybackGain.Interpolate(0, 1, progress, RampCurve.EqualPowerIn);
        var fadingOut = PlaybackGain.Interpolate(1, 0, progress, RampCurve.EqualPowerOut);

        Assert.Equal(1, (fadingIn * fadingIn) + (fadingOut * fadingOut), 10);
    }

    [Fact]
    public void Equal_power_curves_follow_a_quarter_sine_and_cosine()
    {
        Assert.Equal(0, PlaybackGain.Interpolate(0, 1, 0, RampCurve.EqualPowerIn), 10);
        Assert.Equal(Math.Sqrt(0.5), PlaybackGain.Interpolate(0, 1, 0.5, RampCurve.EqualPowerIn), 10);
        Assert.Equal(1, PlaybackGain.Interpolate(0, 1, 1, RampCurve.EqualPowerIn), 10);

        Assert.Equal(1, PlaybackGain.Interpolate(1, 0, 0, RampCurve.EqualPowerOut), 10);
        Assert.Equal(Math.Sqrt(0.5), PlaybackGain.Interpolate(1, 0, 0.5, RampCurve.EqualPowerOut), 10);
        Assert.Equal(0, PlaybackGain.Interpolate(1, 0, 1, RampCurve.EqualPowerOut), 10);
    }

    [Fact]
    public void Equal_power_ramps_start_where_they_are_and_end_on_target()
    {
        // A fade cut short: the incoming gain continues from its current value up to full.
        Assert.Equal(0.6, PlaybackGain.Interpolate(0.6, 1, 0, RampCurve.EqualPowerIn), 10);
        Assert.Equal(1, PlaybackGain.Interpolate(0.6, 1, 1, RampCurve.EqualPowerIn), 10);
        Assert.Equal(0.4, PlaybackGain.Interpolate(0.4, 0, 0, RampCurve.EqualPowerOut), 10);
        Assert.Equal(0, PlaybackGain.Interpolate(0.4, 0, 1, RampCurve.EqualPowerOut), 10);
    }

    [Fact]
    public void Equal_power_fade_out_stays_louder_than_linear_halfway()
    {
        Assert.True(PlaybackGain.Interpolate(1, 0, 0.5, RampCurve.EqualPowerOut) > PlaybackGain.Interpolate(1, 0, 0.5, RampCurve.Linear));
        Assert.True(PlaybackGain.Interpolate(0, 1, 0.5, RampCurve.EqualPowerIn) > PlaybackGain.Interpolate(0, 1, 0.5, RampCurve.Linear));
    }

    [Fact]
    public void Equal_power_ramp_runs_on_the_ramp_clock()
    {
        var ramp = GainRamp.Fixed(0).RampTo(1, TimeSpan.FromSeconds(6), RampCurve.EqualPowerIn, nowMs: 10_000);

        Assert.Equal(0, ramp.ValueAt(10_000), 10);
        Assert.Equal(Math.Sqrt(0.5), ramp.ValueAt(13_000), 10);
        Assert.Equal(1, ramp.ValueAt(16_000), 10);
        Assert.True(ramp.IsDoneAt(16_000));
    }
}
