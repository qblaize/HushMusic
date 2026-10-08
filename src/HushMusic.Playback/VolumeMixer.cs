using HushMusic.Core.Features;

namespace HushMusic.Playback;

/// <summary>
/// Owns the MediaPlayer volume: the user's volume × the volume-normalization gain × the sleep-timer fade gain.
/// Gain changes can be ramped so they never click. Thread-safe; the apply callback runs under this class's lock and
/// must not call back into the player service.
/// </summary>
internal sealed class VolumeMixer : IDisposable
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(15);

    private readonly object _gate = new();
    private readonly Action<double> _apply;
    private readonly Timer _timer;
    private double _volume;
    private GainRamp _loudness = GainRamp.Fixed(1);
    private GainRamp _fade = GainRamp.Fixed(1);
    private double _applied = double.NaN;
    private bool _ticking;
    private bool _disposed;

    public VolumeMixer(double volume, Action<double> apply)
    {
        _volume = volume;
        _apply = apply;
        _timer = new Timer(_ => Tick());
        lock (_gate)
        {
            UpdateNoLock();
        }
    }

    /// <summary>The user's volume (0 – 1).</summary>
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

    /// <summary>Target normalization gain.</summary>
    public double LoudnessGain
    {
        get
        {
            lock (_gate)
            {
                return _loudness.To;
            }
        }
    }

    public void SetLoudnessGain(double gain, TimeSpan ramp)
    {
        lock (_gate)
        {
            _loudness = _loudness.RampTo(gain, ramp, RampCurve.Linear, Environment.TickCount64);
            UpdateNoLock();
        }
    }

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
        var value = PlaybackGain.Combine(_volume, _loudness.ValueAt(now), _fade.ValueAt(now));
        if (!(Math.Abs(value - _applied) < 0.0001))
        {
            _applied = value;
            _apply(value);
        }

        var ramping = !_loudness.IsDoneAt(now) || !_fade.IsDoneAt(now);
        if (ramping != _ticking)
        {
            _ticking = ramping;
            _timer.Change(ramping ? TickInterval : Timeout.InfiniteTimeSpan, ramping ? TickInterval : Timeout.InfiniteTimeSpan);
        }
    }
}
