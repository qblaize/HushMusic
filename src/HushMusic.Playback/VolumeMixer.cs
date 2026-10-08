using HushMusic.Core.Features;

namespace HushMusic.Playback;

/// <summary>
/// Owns the volume of each MediaPlayer (one output per player): the user's volume × the sleep-timer fade gain (shared)
/// × that output's volume-normalization gain × its crossfade gain. Gain changes can be ramped so they never click.
/// Thread-safe; the apply callbacks run under this class's lock and must not call back into the player service.
/// </summary>
internal sealed class VolumeMixer : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);

    private readonly object _gate = new();
    private readonly Output[] _outputs;
    private readonly Timer _timer;
    private double _volume;
    private GainRamp _fade = GainRamp.Fixed(1);
    private bool _ticking;
    private bool _disposed;

    public VolumeMixer(double volume, params Action<double>[] outputs)
    {
        ArgumentOutOfRangeException.ThrowIfZero(outputs.Length);
        _volume = volume;
        _outputs = [.. outputs.Select(apply => new Output(apply))];
        _timer = new Timer(_ => Tick());
        lock (_gate)
        {
            UpdateNoLock();
        }
    }

    /// <summary>The user's volume (0 – 1), for every output.</summary>
    public double Volume
    {
        set
        {
            lock (_gate)
            {
                _volume = value;
                UpdateNoLock();
            }
        }
    }

    /// <summary>Target normalization gain of <paramref name="output"/>.</summary>
    public double LoudnessGain(int output)
    {
        lock (_gate)
        {
            return _outputs[output].Loudness.To;
        }
    }

    public void SetLoudnessGain(int output, double gain, TimeSpan ramp)
    {
        lock (_gate)
        {
            var o = _outputs[output];
            o.Loudness = o.Loudness.RampTo(gain, ramp, RampCurve.Linear, Environment.TickCount64);
            UpdateNoLock();
        }
    }

    /// <summary>The crossfade gain of one output (the outgoing player fades to 0, the incoming one from 0 to 1).</summary>
    public void SetMixGain(int output, double gain, TimeSpan ramp, RampCurve curve)
    {
        lock (_gate)
        {
            var o = _outputs[output];
            o.Mix = o.Mix.RampTo(gain, ramp, curve, Environment.TickCount64);
            UpdateNoLock();
        }
    }

    /// <summary>The sleep-timer fade, applied to every output.</summary>
    public void SetFadeGain(double gain, TimeSpan ramp)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var curve = gain < _fade.ValueAt(now) ? RampCurve.FadeOut : RampCurve.Linear;
            _fade = _fade.RampTo(gain, ramp, curve, now);
            UpdateNoLock();
        }
    }

    /// <summary>Where <paramref name="output"/> is right now, for logs.</summary>
    public MixerLevels Levels(int output)
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            var o = _outputs[output];
            return new MixerLevels(o.Loudness.ValueAt(now), o.Mix.ValueAt(now), _fade.ValueAt(now), o.Applied);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
        }

        _timer.Dispose();
    }

    private void Tick()
    {
        lock (_gate)
        {
            UpdateNoLock();
        }
    }

    private void UpdateNoLock()
    {
        if (_disposed)
        {
            return;
        }

        var now = Environment.TickCount64;
        var fade = _fade.ValueAt(now);
        var ramping = !_fade.IsDoneAt(now);
        foreach (var o in _outputs)
        {
            var value = PlaybackGain.Combine(_volume, o.Loudness.ValueAt(now), fade * o.Mix.ValueAt(now));
            if (!(Math.Abs(value - o.Applied) < 0.0001))
            {
                o.Applied = value;
                o.Apply(value);
            }

            ramping |= !o.Loudness.IsDoneAt(now) || !o.Mix.IsDoneAt(now);
        }

        if (ramping != _ticking)
        {
            _ticking = ramping;
            _timer.Change(ramping ? TickInterval : Timeout.InfiniteTimeSpan, ramping ? TickInterval : Timeout.InfiniteTimeSpan);
        }
    }

    private sealed class Output(Action<double> apply)
    {
        public Action<double> Apply { get; } = apply;

        public GainRamp Loudness { get; set; } = GainRamp.Fixed(1);

        public GainRamp Mix { get; set; } = GainRamp.Fixed(1);

        public double Applied { get; set; } = double.NaN;
    }
}

/// <summary>One output's gains at a moment: normalization, crossfade, sleep-timer fade, and the volume handed to the player.</summary>
internal readonly record struct MixerLevels(double Loudness, double Mix, double Fade, double Applied);
