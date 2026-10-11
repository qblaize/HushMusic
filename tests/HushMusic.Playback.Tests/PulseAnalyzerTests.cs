using HushMusic.Playback.Pulse;
using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class PulseAnalyzerTests
{
    private const int Rate = 44100;
    private const int FramesPerSecond = 25;
    private const int Chunk = Rate / FramesPerSecond;
    private const double KickInterval = 0.5; // 120 BPM

    [Fact]
    public void Fft_puts_a_sine_in_its_bin()
    {
        const int size = 64;
        var fft = new Fft(size);
        var real = new float[size];
        var imaginary = new float[size];
        for (var i = 0; i < size; i++)
        {
            real[i] = (float)Math.Sin(2 * Math.PI * 5 * i / size);
        }

        fft.Transform(real, imaginary);

        var magnitudes = Enumerable.Range(0, size / 2).Select(k => Math.Sqrt((real[k] * real[k]) + (imaginary[k] * imaginary[k]))).ToArray();
        Assert.Equal(5, Array.IndexOf(magnitudes, magnitudes.Max()));
        Assert.Equal(size / 2.0, magnitudes[5], 3);
        Assert.All(magnitudes.Where((_, k) => k != 5), m => Assert.True(m < 1e-3));
    }

    [Fact]
    public void Fft_size_must_be_a_power_of_two() => Assert.Throws<ArgumentOutOfRangeException>(() => new Fft(1000));

    [Fact]
    public void Silence_reads_as_rest()
    {
        var frames = Analyze(_ => 0, seconds: 2);

        Assert.All(frames, f => Assert.Equal(default, f.Levels));
    }

    [Fact]
    public void Kicks_give_one_beat_each_on_time()
    {
        var frames = Analyze(t => Kick(t, 0.8), seconds: 8);

        var beats = BeatTimes(frames);
        var settled = beats.Where(t => t >= 1).ToList();
        Assert.Equal(14, settled.Count); // the kicks at 1.0, 1.5, ... 7.5 s
        foreach (var time in settled)
        {
            var nearestKick = Math.Round(time / KickInterval) * KickInterval;
            Assert.InRange(time - nearestKick, 0, 2.0 / FramesPerSecond);
        }
    }

    [Fact]
    public void Kicks_come_through_over_a_pad_and_hi_hats()
    {
        var frames = Analyze(t => Kick(t, 0.6) + Pad(t, 0.25) + HiHat(t, 0.15), seconds: 8);

        Assert.Equal(14, BeatTimes(frames).Count(t => t >= 1));
    }

    [Fact]
    public void Bass_rises_at_once_and_falls_slower()
    {
        var frames = Analyze(t => Kick(t, 0.8), seconds: 4);

        // The kick at 3.0 s: its frame and the next are near the top, a third of a second later it is mostly gone.
        var atKick = frames.Where(f => f.Time >= 3.0 && f.Time < 3.0 + (2.0 / FramesPerSecond)).Max(f => f.Levels.Bass);
        var before = frames.Last(f => f.Time < 3.0).Levels.Bass;
        var later = frames.First(f => f.Time >= 3.33).Levels.Bass;
        Assert.True(atKick > 0.6, $"peak {atKick}");
        Assert.True(before < 0.25, $"before {before}");
        Assert.InRange(later, 0.02, atKick / 2);
    }

    [Fact]
    public void Volume_does_not_change_the_response()
    {
        var loud = Analyze(t => Kick(t, 0.9) + Pad(t, 0.2), seconds: 6);
        var quiet = Analyze(t => Kick(t, 0.02) + Pad(t, 0.0045), seconds: 6);

        Assert.Equal(BeatTimes(loud), BeatTimes(quiet));
        Assert.Equal(loud.Where(f => f.Time > 2).Max(f => f.Levels.Bass), quiet.Where(f => f.Time > 2).Max(f => f.Levels.Bass), 0.05);
        Assert.Equal(loud.Where(f => f.Time > 2).Average(f => f.Levels.Level), quiet.Where(f => f.Time > 2).Average(f => f.Levels.Level), 0.05);
    }

    [Fact]
    public void Music_without_bass_does_not_pump()
    {
        // Notes on and off four times a second, nothing below 150 Hz.
        var frames = Analyze(t => Pad(t, (t % 0.25) < 0.18 ? 0.5 : 0) + HiHat(t, 0.2), seconds: 6);

        Assert.Empty(BeatTimes(frames));
        Assert.All(frames.Where(f => f.Time > 1), f => Assert.True(f.Levels.Bass < 0.15, $"bass {f.Levels.Bass} at {f.Time:F2}"));
        Assert.True(frames.Where(f => f.Time > 1).Average(f => f.Levels.Level) > 0.3);
    }

    [Fact]
    public void A_held_bass_note_beats_once()
    {
        var frames = Analyze(t => 0.5 * Math.Sin(2 * Math.PI * 60 * t), seconds: 4);

        Assert.True(BeatTimes(frames).Count <= 1);
        Assert.True(frames[^1].Levels.Bass > 0.8);
    }

    [Fact]
    public void Beats_are_never_closer_than_the_minimum_interval()
    {
        // Kicks every 100 ms: far too fast to all count.
        var frames = Analyze(t => 0.8 * Math.Sin(2 * Math.PI * 55 * (t % 0.1)) * Math.Exp(-(t % 0.1) / 0.02), seconds: 4);

        var beats = BeatTimes(frames);
        Assert.NotEmpty(beats);
        Assert.All(beats.Zip(beats.Skip(1)), pair => Assert.True(pair.Second - pair.First >= PulseAnalyzer.MinBeatInterval - 1e-9));
    }

    [Fact]
    public void Levels_settle_to_rest_when_the_audio_stops()
    {
        var analyzer = new PulseAnalyzer(Rate);
        Feed(analyzer, t => Kick(t, 0.8) + Pad(t, 0.2), seconds: 3);

        // Nothing arrives any more (paused, or the stream ended): time passes as silence.
        MusicPulseLevels levels = default;
        for (var i = 0; i < FramesPerSecond * 3; i++)
        {
            levels = analyzer.Analyze(1.0 / FramesPerSecond);
        }

        Assert.True(levels.Bass < 0.01 && levels.Beat < 0.01 && levels.Level < 0.01, levels.ToString());
    }

    [Fact]
    public void The_test_signal_beats_at_120_bpm()
    {
        var signal = new TestSignal(Rate);

        var frames = Analyze(
            seconds: 6,
            fill: buffer =>
            {
                Array.Clear(buffer);
                signal.AddTo(buffer);
            });

        Assert.Equal(10, BeatTimes(frames).Count(t => t >= 1)); // 1.0, 1.5, ... 5.5 s
    }

    // A kick drum: a 55 Hz thump that dies away, every KickInterval seconds.
    private static double Kick(double t, double amplitude)
    {
        var since = t % KickInterval;
        return amplitude * Math.Sin(2 * Math.PI * 55 * since) * Math.Exp(-since / 0.12);
    }

    // A chord well above the bass band.
    private static double Pad(double t, double amplitude) =>
        amplitude * (Math.Sin(2 * Math.PI * 440 * t) + Math.Sin(2 * Math.PI * 554 * t) + Math.Sin(2 * Math.PI * 659 * t)) / 3;

    // A short, bright tick on every eighth note.
    private static double HiHat(double t, double amplitude)
    {
        var since = t % (KickInterval / 2);
        return amplitude * Math.Sin(2 * Math.PI * 7000 * since) * Math.Exp(-since / 0.02);
    }

    private static List<(double Time, MusicPulseLevels Levels)> Analyze(Func<double, double> signal, double seconds) =>
        Feed(new PulseAnalyzer(Rate), signal, seconds);

    private static List<(double Time, MusicPulseLevels Levels)> Analyze(double seconds, Action<float[]> fill)
    {
        var analyzer = new PulseAnalyzer(Rate);
        var frames = new List<(double, MusicPulseLevels)>();
        var buffer = new float[Chunk];
        for (var start = 0; start < seconds * Rate; start += Chunk)
        {
            fill(buffer);
            analyzer.Push(buffer);
            frames.Add(((start + Chunk) / (double)Rate, analyzer.Analyze((double)Chunk / Rate)));
        }

        return frames;
    }

    // Feeds the signal in the chunks the capture thread would, analyzing after each; the time is the end of the chunk.
    private static List<(double Time, MusicPulseLevels Levels)> Feed(PulseAnalyzer analyzer, Func<double, double> signal, double seconds)
    {
        var frames = new List<(double, MusicPulseLevels)>();
        var buffer = new float[Chunk];
        for (var start = 0; start < seconds * Rate; start += Chunk)
        {
            for (var i = 0; i < Chunk; i++)
            {
                buffer[i] = (float)signal((start + i) / (double)Rate);
            }

            analyzer.Push(buffer);
            frames.Add(((start + Chunk) / (double)Rate, analyzer.Analyze((double)Chunk / Rate)));
        }

        return frames;
    }

    // When a new beat starts: the beat level jumps up.
    private static List<double> BeatTimes(List<(double Time, MusicPulseLevels Levels)> frames)
    {
        var times = new List<double>();
        for (var i = 1; i < frames.Count; i++)
        {
            if (frames[i].Levels.Beat > frames[i - 1].Levels.Beat + 0.1)
            {
                times.Add(Math.Round(frames[i].Time, 6));
            }
        }

        if (frames.Count > 0 && frames[0].Levels.Beat > 0.1)
        {
            times.Insert(0, Math.Round(frames[0].Time, 6));
        }

        return times;
    }
}
