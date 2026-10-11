using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HushMusic.Playback.Pulse;

/// <summary>
/// <see cref="IMusicPulse"/> on Windows' process loopback capture (<see cref="ProcessLoopbackCapture"/>) and
/// <see cref="PulseAnalyzer"/>. While anyone listens, one background thread captures the app's audio: it wakes every
/// 20 ms to read what arrived and analyzes the latest samples <see cref="LevelsPerSecond"/> times a second. (Waking for
/// every 10 ms packet the engine signals cost three times the CPU.) When the last listener goes, the stream stops and the
/// thread ends.
/// </summary>
public sealed class MusicPulse : IMusicPulse, IDisposable
{
    public const int LevelsPerSecond = 25;

    // The engine keeps only about 30 ms of the stream, so it is read every 20 ms and analyzed every other time.
    private const int ReadsPerAnalysis = 2;
    private const int CoInitMultithreaded = 0;

    private static readonly TimeSpan ReadInterval = TimeSpan.FromSeconds(1.0 / (LevelsPerSecond * ReadsPerAnalysis));
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(10);

    private readonly ILogger<MusicPulse> _logger;
    private readonly bool _testSignal = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_PULSE") == "1";
    private readonly object _gate = new();
    private Action<MusicPulseLevels>[] _listeners = [];
    private Run? _run;
    private int _availability;

    public MusicPulse(ILogger<MusicPulse> logger)
    {
        _logger = logger;
        if (!ProcessLoopbackCapture.IsSupported)
        {
            _availability = (int)MusicPulseAvailability.NeedsNewerWindows;
            logger.LogInformation(
                "Pulse to the music is unavailable: capturing the app's own audio needs Windows build {Build} or later (this is {Version})",
                ProcessLoopbackCapture.MinimumBuild,
                Environment.OSVersion.Version);
        }
    }

    public MusicPulseAvailability Availability => (MusicPulseAvailability)Volatile.Read(ref _availability);

    public event EventHandler? AvailabilityChanged;

    public IDisposable Listen(Action<MusicPulseLevels> levels)
    {
        ArgumentNullException.ThrowIfNull(levels);
        lock (_gate)
        {
            _listeners = [.. _listeners, levels];
            if (_run is null && Availability == MusicPulseAvailability.Available)
            {
                _run = new Run(this);
                _run.Start();
            }
        }

        return new Lease(this, levels);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _listeners = [];
            _run?.Stop();
            _run = null;
        }
    }

    private void Remove(Action<MusicPulseLevels> levels)
    {
        lock (_gate)
        {
            var index = Array.IndexOf(_listeners, levels);
            if (index < 0)
            {
                return;
            }

            _listeners = [.. _listeners[..index], .. _listeners[(index + 1)..]];
            if (_listeners.Length == 0)
            {
                _run?.Stop();
                _run = null;
            }
        }
    }

    // The capture thread's loop: read what arrived, analyze every other time, until the run is stopped.
    private void Capture(Run run)
    {
        var com = CoInitializeEx(IntPtr.Zero, CoInitMultithreaded);
        ProcessLoopbackCapture? capture = null;
        try
        {
            capture = ProcessLoopbackCapture.Activate(ActivationTimeout);
            if (_testSignal)
            {
                capture.TestSignal = new TestSignal(ProcessLoopbackCapture.SampleRate);
            }

            capture.Start();

            _logger.LogDebug("Music pulse: capturing the app's audio{Test}", _testSignal ? " with the test signal" : string.Empty);

            var analyzer = new PulseAnalyzer(ProcessLoopbackCapture.SampleRate);
            var clock = Stopwatch.StartNew();
            var previous = TimeSpan.Zero;
            var statsDue = StatsInterval;
            var reads = 0;
            var analyses = 0;
            var threadTime = ThreadTime();
            while (!run.Stopping.WaitOne(ReadInterval))
            {
                capture.Read(analyzer);
                if (++reads % ReadsPerAnalysis != 0)
                {
                    continue;
                }

                var now = clock.Elapsed;
                var levels = analyzer.Analyze((now - previous).TotalSeconds);
                analyses++;
                previous = now;
                foreach (var listener in Volatile.Read(ref _listeners))
                {
                    listener(levels);
                }

                if (now >= statsDue && _logger.IsEnabled(LogLevel.Debug))
                {
                    statsDue = now + StatsInterval;
                    var (frames, silent, peak) = capture.TakeStats();
                    var cpu = ThreadTime();
                    _logger.LogDebug(
                        "Music pulse: {Frames} frames captured ({Silent} marked silent), peak {Peak:F1} dBFS; {Analyses} analyses; capture thread CPU {Cpu:F0} ms; last bass {Bass:F2}, beat {Beat:F2}, level {Level:F2}",
                        frames,
                        silent,
                        peak > 0 ? 20 * Math.Log10(peak) : double.NegativeInfinity,
                        analyses,
                        (cpu - threadTime).TotalMilliseconds,
                        levels.Bass,
                        levels.Beat,
                        levels.Level);
                    threadTime = cpu;
                    analyses = 0;
                }
            }

            _logger.LogDebug("Music pulse: stopped capturing");
        }
        catch (Exception ex)
        {
            // Activation is refused on some systems (or the audio service went away): stop trying for this session.
            lock (_gate)
            {
                if (_run == run)
                {
                    _run = null;
                }
            }

            if (Interlocked.CompareExchange(ref _availability, (int)MusicPulseAvailability.Failed, (int)MusicPulseAvailability.Available) == (int)MusicPulseAvailability.Available)
            {
                _logger.LogWarning(ex, "Pulse to the music is unavailable: Windows didn't let the app capture its own audio");
                AvailabilityChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        finally
        {
            capture?.Dispose();
            run.Exit();
            if (com >= 0)
            {
                CoUninitialize();
            }
        }
    }

    // CPU time (kernel and user) of the calling thread, for the debug log.
    private static TimeSpan ThreadTime() =>
        GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? TimeSpan.FromTicks(kernel + user) : TimeSpan.Zero;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int CoInitializeEx(IntPtr reserved, int coInit);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern void CoUninitialize();

    /// <summary>One capture session: its thread and the event that ends it.</summary>
    private sealed class Run
    {
        private readonly ManualResetEvent _stop = new(false);
        private readonly Thread _thread;
        private readonly object _gate = new();
        private bool _exited;

        public Run(MusicPulse owner)
        {
            _thread = new Thread(() => owner.Capture(this))
            {
                IsBackground = true,
                Name = "Music pulse",
            };
        }

        public WaitHandle Stopping => _stop;

        public void Start() => _thread.Start();

        public void Stop()
        {
            lock (_gate)
            {
                if (!_exited)
                {
                    _stop.Set();
                }
            }
        }

        public void Exit()
        {
            lock (_gate)
            {
                _exited = true;
                _stop.Dispose();
            }
        }
    }

    private sealed class Lease(MusicPulse owner, Action<MusicPulseLevels> levels) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Remove(levels);
            }
        }
    }
}
