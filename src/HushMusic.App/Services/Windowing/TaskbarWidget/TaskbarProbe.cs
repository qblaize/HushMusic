using Microsoft.Extensions.Logging;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// Runs <see cref="TaskbarAutomation"/> on its own MTA thread, on request, and posts <paramref name="doneMessage"/> to
/// <paramref name="notify"/> with each result. The UI Automation calls go to Explorer and can stall while it is busy, so
/// they never run on the widget thread (which Explorer's taskbar waits on) or the UI thread.
/// </summary>
internal sealed class TaskbarProbe(IntPtr notify, uint doneMessage, ILogger logger) : IDisposable
{
    private const uint CoInitMultithreaded = 0x0;

    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private IntPtr _taskbar;
    private volatile bool _stopped;
    private volatile ProbeResult? _latest;

    /// <summary>The most recent answer (any thread).</summary>
    public ProbeResult? Latest => _latest;

    /// <summary>Reads the taskbar soon. Repeated requests while a read runs are coalesced.</summary>
    public void Request(IntPtr taskbar)
    {
        if (_stopped)
        {
            return;
        }

        Interlocked.Exchange(ref _taskbar, taskbar);
        if (_thread is null)
        {
            _thread = new Thread(Run) { IsBackground = true, Name = "Taskbar layout probe" };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }

        _wake.Set();
    }

    // Doesn't wait: the thread may be inside a slow UI Automation call. It is a background thread and exits on its own.
    public void Dispose()
    {
        _stopped = true;
        _wake.Set();
    }

    private void Run()
    {
        var initialized = WidgetNative.CoInitializeEx(IntPtr.Zero, CoInitMultithreaded) >= 0;
        var automation = new TaskbarAutomation();
        try
        {
            while (true)
            {
                _wake.WaitOne();
                if (_stopped)
                {
                    return;
                }

                var taskbar = Interlocked.CompareExchange(ref _taskbar, IntPtr.Zero, IntPtr.Zero);
                TaskbarReading? reading = null;
                try
                {
                    reading = automation.Query(taskbar);
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Reading the taskbar layout failed");
                }

                _latest = new ProbeResult(taskbar, reading?.Elements, reading?.TrayLeft);
                if (!_stopped)
                {
                    Win32.PostMessage(notify, doneMessage, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }
        finally
        {
            // The wake event is left to its finalizer: Dispose may still set it after this thread has seen the stop.
            automation.Dispose();
            if (initialized)
            {
                WidgetNative.CoUninitialize();
            }
        }
    }
}

/// <summary>A UI Automation read of one taskbar window; <see cref="Elements"/> is null when it failed.</summary>
/// <param name="TrayLeft">Where the notification area starts, in screen pixels, when UI Automation showed it.</param>
internal sealed record ProbeResult(IntPtr Taskbar, IReadOnlyList<TaskbarElement>? Elements, int? TrayLeft);
