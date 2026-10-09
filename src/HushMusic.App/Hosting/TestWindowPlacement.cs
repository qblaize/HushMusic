using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.App.Services.Windowing.TaskbarWidget;

namespace HushMusic.App.Hosting;

/// <summary>For automated test instances (HUSHMUSIC_TEST_BACKGROUND=1): show the window at the bottom of the z-order without activating it.</summary>
internal static class TestWindowPlacement
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const int SwShowMinNoActive = 7;
    private const int SwShowNoActivate = 4;
    private static readonly IntPtr HwndBottom = new(1);
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _testTimer;
    private static Microsoft.UI.Dispatching.DispatcherQueueTimer? _flyoutTimer;

    // Kept in a field: an unreferenced DispatcherQueueTimer can be collected before it fires.
    private static readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> s_windowTimers = [];

    public static void ShowInBackground(Window window)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        window.AppWindow.Show(activateWindow: false);
        SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    /// <summary>Back to the bottom of the z-order (this also drops always-on-top, e.g. after entering the mini player).</summary>
    public static void SendToBack(Window window) =>
        SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(window), HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    /// <summary>
    /// HUSHMUSIC_TEST_MINI=1 enters the mini player shortly after startup; =controls also keeps its hover controls
    /// visible (screenshots can't hover).
    /// </summary>
    public static void RunTestHooks(MainWindow window)
    {
        RunTaskbarTestHooks(window);
        RunWindowScript(window);
        var mini = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_MINI");
        if (mini is not ("1" or "controls"))
        {
            return;
        }

        // Kept in a field: an unreferenced DispatcherQueueTimer can be collected before it fires.
        _testTimer = window.DispatcherQueue.CreateTimer();
        _testTimer.Interval = TimeSpan.FromSeconds(3);
        _testTimer.IsRepeating = false;
        _testTimer.Tick += (_, _) =>
        {
            window.Modes.EnterMiniPlayer();
            if (mini == "controls")
            {
                window.MiniPlayer?.Activate(TimeSpan.FromHours(1));
            }
        };
        _testTimer.Start();
    }

    /// <summary>
    /// HUSHMUSIC_TEST_WIDGET_DUMP=&lt;folder&gt; draws the taskbar player's states to PNG sheets (offscreen, nothing on the
    /// taskbar); HUSHMUSIC_TEST_FLYOUT=1 opens the taskbar player's flyout without activating it, after the restored
    /// session has loaded.
    /// </summary>
    private static void RunTaskbarTestHooks(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_WIDGET_DUMP") is { Length: > 0 } folder)
        {
            var logger = App.GetService<ILogger<TaskbarPlayerService>>();
            _ = TaskbarWidgetDump.RunAsync(folder, Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_WIDGET_DUMP_ART"), logger);
        }

        if (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_FLYOUT") == "1")
        {
            _flyoutTimer = window.DispatcherQueue.CreateTimer();
            _flyoutTimer.Interval = TimeSpan.FromSeconds(5);
            _flyoutTimer.IsRepeating = false;
            _flyoutTimer.Tick += (_, _) => App.GetService<TaskbarPlayerService>().OpenFlyoutForTest();
            _flyoutTimer.Start();
        }
    }

    /// <summary>
    /// HUSHMUSIC_TEST_WINDOW="minimize@30,restore@45,hide@60,show@90" changes the window's state at those seconds after
    /// startup, without activating it: "hide" hides it as closing to the notification area does (test instances have no
    /// tray icon), "show" brings it back as the tray icon does, "minimize" / "restore" use the taskbar states. Each step
    /// is logged, and so is the time from "show" / "restore" to the next rendered frame.
    /// </summary>
    private static void RunWindowScript(MainWindow window)
    {
        if (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_WINDOW") is not { Length: > 0 } script)
        {
            return;
        }

        var logger = App.GetService<ILogger<MainWindow>>();
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        foreach (var step in script.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = step.Split('@');
            if (parts.Length != 2 || !double.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            var action = parts[0];
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(seconds);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                logger.LogInformation("Test window step: {Action}", action);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                switch (action)
                {
                    case "hide":
                        window.AppWindow.Hide();
                        break;
                    case "show":
                        window.Modes.ShowMainWindow();
                        LogNextFrame(logger, clock, action);
                        break;
                    case "minimize":
                        ShowWindow(hwnd, SwShowMinNoActive);
                        break;
                    case "restore":
                        ShowWindow(hwnd, SwShowNoActivate);
                        SendToBack(window);
                        LogNextFrame(logger, clock, action);
                        break;
                }
            };
            s_windowTimers.Add(timer);
            timer.Start();
        }
    }

    private static void LogNextFrame(ILogger logger, System.Diagnostics.Stopwatch clock, string action)
    {
        void OnRendered(object? sender, Microsoft.UI.Xaml.Media.RenderedEventArgs e)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnRendered;
            logger.LogInformation("Test window step: {Action} rendered after {Milliseconds} ms", action, clock.ElapsedMilliseconds);
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnRendered;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);
}
