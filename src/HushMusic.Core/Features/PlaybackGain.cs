namespace HushMusic.Core.Features;

/// <summary>
/// Gain maths for volume normalization, the sleep-timer fade and crossfades. Gains are linear amplitude factors (0 – 1)
/// multiplied onto the user's volume; the user's volume itself is never changed.
/// </summary>
public static class PlaybackGain
{
    /// <summary>
    /// YouTube's rule: a track louder than the reference level is turned down by its excess (10^(−dB/20));
    /// quieter tracks and unknown loudness keep gain 1 (never boosted).
    /// </summary>
    public static double ForLoudness(double? loudnessDb) =>
        loudnessDb is { } db && double.IsFinite(db) && db > 0 ? Math.Pow(10, -db / 20) : 1;

    /// <summary>The volume handed to the audio output: user volume × normalization gain × fade gain, clamped to 0 – 1.</summary>
    public static double Combine(double volume, double loudnessGain, double fadeGain)
    {
        var value = volume * loudnessGain * fadeGain;
        return double.IsFinite(value) ? Math.Clamp(value, 0, 1) : 0;
    }

    /// <summary>Gain at <paramref name="progress"/> (0 – 1) of a ramp from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static double Interpolate(double from, double to, double progress, RampCurve curve)
    {
        var p = double.IsFinite(progress) ? Math.Clamp(progress, 0, 1) : 1;
        var remaining = curve switch
        {
            // The remaining distance shrinks with (1 − p)², so a fade to silence drops evenly in loudness
            // instead of lingering near full volume and cutting off at the end.
            RampCurve.FadeOut => (1 - p) * (1 - p),

            // A quarter sine/cosine: the two halves of a crossfade keep in² + out² = 1, so the blend holds its loudness
            // instead of dipping in the middle as two linear ramps would (-3 dB each at the halfway point).
            RampCurve.EqualPowerIn => 1 - Math.Sin(p * Math.PI / 2),
            RampCurve.EqualPowerOut => Math.Cos(p * Math.PI / 2),
            _ => 1 - p,
        };
        return to + ((from - to) * remaining);
    }
}

public enum RampCurve
{
    Linear,

    /// <summary>Quadratic, for fades towards silence.</summary>
    FadeOut,

    /// <summary>The incoming half of an equal-power crossfade: sin(p·π/2) of the way from start to target.</summary>
    EqualPowerIn,

    /// <summary>The outgoing half of an equal-power crossfade: cos(p·π/2) of the distance left to the target.</summary>
    EqualPowerOut,
}

/// <summary>A gain moving from <see cref="From"/> to <see cref="To"/> over <see cref="DurationMs"/>, on a millisecond clock.</summary>
public readonly record struct GainRamp(double From, double To, long StartMs, long DurationMs, RampCurve Curve)
{
    public static GainRamp Fixed(double gain) => new(gain, gain, 0, 0, RampCurve.Linear);

    public double ValueAt(long nowMs)
    {
        if (IsDoneAt(nowMs))
        {
            return To;
        }

        return nowMs <= StartMs ? From : PlaybackGain.Interpolate(From, To, (double)(nowMs - StartMs) / DurationMs, Curve);
    }

    public bool IsDoneAt(long nowMs) => DurationMs <= 0 || nowMs >= StartMs + DurationMs;

    /// <summary>A ramp that starts at this ramp's current value, so changing direction mid-ramp never jumps.</summary>
    public GainRamp RampTo(double to, TimeSpan duration, RampCurve curve, long nowMs) =>
        new(ValueAt(nowMs), to, nowMs, Math.Max(0, (long)duration.TotalMilliseconds), curve);
}
