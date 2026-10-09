using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;

namespace HushMusic.App.Services.Performance;

/// <summary>
/// Times the start, from process start to the main window's first rendered frame, by phase. Logged at Debug
/// (Settings → Diagnostics → log level).
/// </summary>
internal sealed class StartupTiming
{
    private readonly long _launched = Stopwatch.GetTimestamp();
    private readonly TimeSpan _beforeLaunch;
    private readonly List<string> _phases = [];
    private TimeSpan _last;
    private ILogger? _logger;
    private bool _firstFrameSeen;

    private StartupTiming(TimeSpan beforeLaunch)
    {
        _beforeLaunch = beforeLaunch;
        _last = beforeLaunch;
    }

    /// <summary>Call first thing in OnLaunched: what came before (runtime, entry point, XAML) counts as one phase.</summary>
    public static StartupTiming Begin()
    {
        TimeSpan beforeLaunch;
        try
        {
            using var process = Process.GetCurrentProcess();
            beforeLaunch = DateTime.Now - process.StartTime;
        }
        catch (Exception)
        {
            beforeLaunch = TimeSpan.Zero;
        }

        return new StartupTiming(beforeLaunch);
    }

    private TimeSpan SinceProcessStart => _beforeLaunch + Stopwatch.GetElapsedTime(_launched);

    /// <summary>Ends a phase. Phases that end after the first frame are logged on their own.</summary>
    public void Mark(string phase)
    {
        var now = SinceProcessStart;
        if (_firstFrameSeen)
        {
            _logger?.LogDebug("Startup: {Phase} done {Elapsed:N0} ms after process start", phase, now.TotalMilliseconds);
            return;
        }

        _phases.Add($"{phase} {(now - _last).TotalMilliseconds:N0}");
        _last = now;
    }

    /// <summary>Logs the phases once the main window has rendered its first frame. UI thread.</summary>
    public void LogFirstFrame(ILogger logger)
    {
        _logger = logger;
        CompositionTarget.Rendered += OnRendered;
    }

    private void OnRendered(object? sender, RenderedEventArgs e)
    {
        CompositionTarget.Rendered -= OnRendered;
        Mark("first frame");
        _firstFrameSeen = true;
        // Precompiled (ReadyToRun) builds leave the JIT little to do before the first frame.
        _logger?.LogDebug(
            "Startup: first frame {Elapsed:N0} ms after process start (launch {Launch:N0}, {Phases} ms); {Methods:N0} methods JIT-compiled in {Jit:N0} ms",
            SinceProcessStart.TotalMilliseconds,
            _beforeLaunch.TotalMilliseconds,
            string.Join(", ", _phases),
            System.Runtime.JitInfo.GetCompiledMethodCount(),
            System.Runtime.JitInfo.GetCompilationTime().TotalMilliseconds);
    }
}
