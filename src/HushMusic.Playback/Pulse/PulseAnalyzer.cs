namespace HushMusic.Playback.Pulse;

/// <summary>
/// Turns the app's audio into a few smooth numbers for visuals (<see cref="MusicPulseLevels"/>): the bass, a beat
/// impulse and the overall level. Pure and single-threaded: the capture thread pushes mono samples, then asks for the
/// levels a few dozen times a second.
/// </summary>
/// <remarks>
/// Each analysis looks at the last <see cref="WindowSize"/> samples (46 ms at 44.1 kHz) through a Hann window. Levels
/// are relative to the loudest recent moment of the music (an automatic gain that forgets over several seconds), so they
/// don't depend on the volume, and quiet passages read as zero. A beat is an onset in the bass band: bass energy rising
/// well above how it has been rising lately (a few times the average rise), at most one per
/// <see cref="MinBeatInterval"/>. Analyses overlap little at 25 a second, so a kick's rise can straddle two of them: the
/// rise is measured against the lower of the two previous energies.
/// </remarks>
internal sealed class PulseAnalyzer
{
    public const int WindowSize = 2048;

    /// <summary>The closest two beats can be, in seconds (240 BPM).</summary>
    public const double MinBeatInterval = 0.25;

    // Kick drums and bass lines.
    private const double BassLowHz = 35;
    private const double BassHighHz = 150;

    // Envelopes, as time constants in seconds: the bass follows a kick within a frame and lets go over a quarter of a
    // second; the level moves slower; a beat fades out in about half a second.
    private const double BassAttack = 0.02;
    private const double BassRelease = 0.25;
    private const double LevelAttack = 0.1;
    private const double LevelRelease = 0.6;
    private const double BeatDecay = 0.22;

    // How quickly the "loudest recent moment" forgets a loud part, and the window the beat threshold averages over.
    private const double ReferenceRelease = 8;
    private const double RiseMemory = 1.5;

    // A beat: the bass energy rises by more than BeatRiseOverAverage times its recent average rise, and at least
    // MinBeatRise, to at least MinBeatEnergy. (An average, not mean plus deviations: the kicks themselves would inflate
    // the deviation and hide the softer kick after a loud one.)
    private const double BeatRiseOverAverage = 2.5;
    private const double MinBeatRise = 0.1;
    private const double MinBeatEnergy = 0.3;

    // Below about -70 dBFS the music counts as silent. The references never fall below a few times that, so near-
    // silence (a fade, a quiet intro) is never amplified to full scale.
    private const double SilenceRms = 3e-4;
    private const double MinReference = 2e-3;

    // Music without much bass (piano, voice) shouldn't pump: its bass is measured against at least this share of the
    // overall level instead of its own (small) peak.
    private const double BassShareOfLevel = 0.35;

    private readonly Fft _fft = new(WindowSize);
    private readonly float[] _window = new float[WindowSize];
    private readonly double _windowPower;
    private readonly float[] _history = new float[WindowSize];
    private readonly float[] _real = new float[WindowSize];
    private readonly float[] _imaginary = new float[WindowSize];
    private readonly int _bassFrom;
    private readonly int _bassTo;

    // The oldest sample in the history (and where the next one goes), and how many arrived since the last analysis.
    private int _write;
    private int _fresh;

    private double _bassReference;
    private double _levelReference;
    private double _previousBassEnergy;
    private double _earlierBassEnergy;
    private double _averageRise;
    private double _sinceBeat = double.PositiveInfinity;
    private double _bass;
    private double _beat;
    private double _level;

    public PulseAnalyzer(int sampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 8000);
        SampleRate = sampleRate;
        for (var i = 0; i < WindowSize; i++)
        {
            _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / WindowSize)));
            _windowPower += _window[i] * _window[i];
        }

        var binHz = (double)sampleRate / WindowSize;
        _bassFrom = Math.Max(1, (int)Math.Round(BassLowHz / binHz));
        _bassTo = Math.Max(_bassFrom, (int)Math.Round(BassHighHz / binHz));
    }

    public int SampleRate { get; }

    /// <summary>Appends mono samples (-1 – 1).</summary>
    public void Push(ReadOnlySpan<float> samples)
    {
        _fresh += samples.Length;
        if (samples.Length > WindowSize)
        {
            samples = samples[^WindowSize..];
        }

        while (!samples.IsEmpty)
        {
            var count = Math.Min(samples.Length, WindowSize - _write);
            samples[..count].CopyTo(_history.AsSpan(_write));
            _write = (_write + count) % WindowSize;
            samples = samples[count..];
        }
    }

    /// <summary>Appends <paramref name="count"/> samples of silence.</summary>
    public void PushSilence(int count)
    {
        _fresh += count;
        count = Math.Min(count, WindowSize);
        while (count > 0)
        {
            var run = Math.Min(count, WindowSize - _write);
            _history.AsSpan(_write, run).Clear();
            _write = (_write + run) % WindowSize;
            count -= run;
        }
    }

    /// <summary>
    /// Analyzes the latest samples, <paramref name="elapsedSeconds"/> after the previous call. When nothing arrived in
    /// between (the app went quiet), that time counts as silence.
    /// </summary>
    public MusicPulseLevels Analyze(double elapsedSeconds)
    {
        var dt = Math.Clamp(elapsedSeconds, 0.001, 0.5);
        if (_fresh == 0)
        {
            PushSilence((int)(dt * SampleRate));
        }

        _fresh = 0;

        // The history, oldest first, through the window; the overall level on the way.
        double sumOfSquares = 0;
        for (var i = 0; i < WindowSize; i++)
        {
            var sample = _history[(_write + i) % WindowSize];
            sumOfSquares += sample * sample;
            _real[i] = sample * _window[i];
            _imaginary[i] = 0;
        }

        var rms = Math.Sqrt(sumOfSquares / WindowSize);

        _fft.Transform(_real, _imaginary);
        double bassPower = 0;
        for (var k = _bassFrom; k <= _bassTo; k++)
        {
            bassPower += (_real[k] * _real[k]) + (_imaginary[k] * _imaginary[k]);
        }

        // Parseval: the RMS of the bass band alone, in the same units as the overall RMS.
        var bassRms = Math.Sqrt(2 * bassPower / (WindowSize * _windowPower));

        var forget = Math.Exp(-dt / ReferenceRelease);
        _levelReference = Math.Max(Math.Max(rms, _levelReference * forget), MinReference);
        _bassReference = Math.Max(Math.Max(bassRms, _bassReference * forget), Math.Max(MinReference, BassShareOfLevel * _levelReference));

        var silent = rms < SilenceRms;
        var level = silent ? 0 : Math.Min(1, rms / _levelReference);
        var bassAmplitude = silent ? 0 : Math.Min(1, bassRms / _bassReference);
        var bassEnergy = bassAmplitude * bassAmplitude;

        // A beat: the bass energy jumps by much more than it has been moving lately.
        var rise = Math.Max(0, bassEnergy - Math.Min(_previousBassEnergy, _earlierBassEnergy));
        _earlierBassEnergy = _previousBassEnergy;
        _previousBassEnergy = bassEnergy;
        var threshold = Math.Max(MinBeatRise, BeatRiseOverAverage * _averageRise);
        _sinceBeat += dt;
        _beat *= Math.Exp(-dt / BeatDecay);
        if (rise > threshold && bassEnergy >= MinBeatEnergy && _sinceBeat >= MinBeatInterval)
        {
            _beat = Math.Max(_beat, bassEnergy);
            _sinceBeat = 0;
        }

        _averageRise += (rise - _averageRise) * (1 - Math.Exp(-dt / RiseMemory));

        _bass = Follow(_bass, bassEnergy, dt, BassAttack, BassRelease);
        _level = Follow(_level, level, dt, LevelAttack, LevelRelease);
        return new MusicPulseLevels((float)_bass, (float)_beat, (float)_level);
    }

    // A one-pole envelope: towards the target with the attack time constant when rising, the release one when falling.
    private static double Follow(double current, double target, double dt, double attack, double release) =>
        current + ((target - current) * (1 - Math.Exp(-dt / (target > current ? attack : release))));
}
