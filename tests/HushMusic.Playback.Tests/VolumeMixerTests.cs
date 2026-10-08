using HushMusic.Core.Features;
using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class VolumeMixerTests
{
    private readonly double[] _applied = [double.NaN, double.NaN];

    [Fact]
    public void Each_output_gets_the_users_volume_on_start()
    {
        using var mixer = Create(0.8);

        Assert.Equal(0.8, _applied[0], 10);
        Assert.Equal(0.8, _applied[1], 10);
    }

    [Fact]
    public void Normalization_and_crossfade_gains_are_per_output()
    {
        using var mixer = Create(0.8);

        mixer.SetLoudnessGain(0, 0.5, TimeSpan.Zero);
        mixer.SetMixGain(1, 0.25, TimeSpan.Zero, RampCurve.Linear);

        Assert.Equal(0.4, _applied[0], 10);
        Assert.Equal(0.2, _applied[1], 10);
        Assert.Equal(0.5, mixer.LoudnessGain(0));
        Assert.Equal(1, mixer.LoudnessGain(1));
    }

    [Fact]
    public void Volume_and_the_sleep_fade_apply_to_both_outputs()
    {
        using var mixer = Create(1);
        mixer.SetLoudnessGain(0, 0.5, TimeSpan.Zero);

        mixer.Volume = 0.5;
        Assert.Equal(0.25, _applied[0], 10);
        Assert.Equal(0.5, _applied[1], 10);

        mixer.SetFadeGain(0.5, TimeSpan.Zero);
        Assert.Equal(0.125, _applied[0], 10);
        Assert.Equal(0.25, _applied[1], 10);

        var levels = mixer.Levels(1);
        Assert.Equal(new MixerLevels(1, 1, 0.5, 0.25), levels);
    }

    [Fact]
    public async Task A_ramped_crossfade_reaches_its_targets()
    {
        using var mixer = Create(1);
        mixer.SetMixGain(1, 0, TimeSpan.Zero, RampCurve.Linear);

        mixer.SetMixGain(0, 0, TimeSpan.FromMilliseconds(60), RampCurve.EqualPowerOut);
        mixer.SetMixGain(1, 1, TimeSpan.FromMilliseconds(60), RampCurve.EqualPowerIn);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!(Math.Abs(_applied[0]) < 0.001 && Math.Abs(_applied[1] - 1) < 0.001))
        {
            Assert.True(DateTime.UtcNow < deadline, "The ramps did not finish");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, mixer.Levels(0).Mix, 2);
        Assert.Equal(1, mixer.Levels(1).Mix, 2);
    }

    private VolumeMixer Create(double volume) => new(volume, v => _applied[0] = v, v => _applied[1] = v);
}
