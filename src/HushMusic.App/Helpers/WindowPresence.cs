using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Windowing;

namespace HushMusic.App.Helpers;

/// <summary>
/// Whether the main window is on screen, for UI that should let go of what it shows (artwork, animations) while
/// nobody can see it: minimized, or hidden in the notification area. A UI-thread view of
/// <see cref="IWindowModeService.WindowVisibility"/>; the mini player counts as shown.
/// </summary>
/// <remarks>
/// Animations also stop while other windows cover all of the shown window (<see cref="IsCovered"/>): a running animation
/// redraws the whole window every frame, seen or not. Coverage is checked once a second, and when the window is
/// activated, but only while something listens to <see cref="CoverageChanged"/> (a running <see cref="AnimationGate"/>).
/// </remarks>
internal static class WindowPresence
{
    private static readonly TimeSpan CoverageInterval = TimeSpan.FromSeconds(1);

    // Test instances open behind every other window: HUSHMUSIC_TEST_COVERAGE=off lets them animate (and be measured).
    private static readonly bool s_coverageOff = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_COVERAGE") == "off";

    private static IWindowModeService? s_windowMode;
    private static bool s_resolved;
    private static EventHandler? s_changed;
    private static EventHandler? s_coverageChanged;
    private static DispatcherQueueTimer? s_coverageTimer;
    private static ILogger? s_logger;

    /// <summary>True while the main window (or the mini player) is on screen.</summary>
    public static bool IsShown => WindowMode()?.IsWindowVisible ?? true;

    /// <summary>
    /// The main window is shown, but other windows cover all of it. Known only while something listens to
    /// <see cref="CoverageChanged"/>; false otherwise.
    /// </summary>
    public static bool IsCovered { get; private set; }

    /// <summary>
    /// The main window is minimized or hidden and the element is in it (or not in any window yet: it is checked again
    /// when it loads). Elements of other windows (the taskbar player's flyout) don't count: they come and go with their
    /// own window.
    /// </summary>
    public static bool Hides(UIElement element) => !IsShown && InMainWindow(element);

    /// <summary>
    /// For animations: <see cref="Hides"/>, or the element is in the main window and other windows cover all of it
    /// (only known to listeners of <see cref="CoverageChanged"/>).
    /// </summary>
    public static bool HidesAnimation(UIElement element) => (!IsShown || IsCovered) && InMainWindow(element);

    /// <summary>Raised on the UI thread after the window was shown, minimized, restored or hidden.</summary>
    public static event EventHandler? Changed
    {
        add
        {
            WindowMode();
            s_changed += value;
        }

        remove => s_changed -= value;
    }

    /// <summary>
    /// Raised on the UI thread when <see cref="IsCovered"/> changes while the window is shown. Listening starts the
    /// coverage checks; a new listener reads <see cref="IsCovered"/> right after subscribing (it is current by then).
    /// </summary>
    public static event EventHandler? CoverageChanged
    {
        add
        {
            WindowMode();
            s_coverageChanged += value;
            UpdateCoverageWatch();
        }

        remove
        {
            s_coverageChanged -= value;
            UpdateCoverageWatch();
        }
    }

    private static bool InMainWindow(UIElement element) =>
        element.XamlRoot is not { } root || root == App.MainWindow?.Content?.XamlRoot;

    private static IWindowModeService? WindowMode()
    {
        if (!s_resolved && App.Services is { } services)
        {
            s_resolved = true;
            s_windowMode = services.GetService<IWindowModeService>();
            s_logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(WindowPresence).FullName!);
            if (s_windowMode is not null)
            {
                s_windowMode.VisibilityChanged += (_, _) =>
                {
                    // Coverage first, so the listeners below already see it for the new state.
                    UpdateCoverageWatch();
                    s_changed?.Invoke(null, EventArgs.Empty);
                };
            }
        }

        return s_windowMode;
    }

    // Checks run while someone listens and the window is shown. Starting or stopping updates IsCovered quietly: the
    // listener that triggered it (or every listener, after a visibility change) reads it right after.
    private static void UpdateCoverageWatch()
    {
        var window = App.MainWindow;
        if (s_coverageOff || s_coverageChanged is null || window is null || !IsShown)
        {
            s_coverageTimer?.Stop();
            IsCovered = false;
            return;
        }

        if (s_coverageTimer is null)
        {
            s_coverageTimer = window.DispatcherQueue.CreateTimer();
            s_coverageTimer.Interval = CoverageInterval;
            s_coverageTimer.Tick += (_, _) => CheckCoverage(notify: true);

            // Brought to the front (or another window activated): no need to wait for the next check.
            window.Activated += (_, _) =>
            {
                if (s_coverageTimer.IsRunning)
                {
                    CheckCoverage(notify: true);
                }
            };
        }

        if (!s_coverageTimer.IsRunning)
        {
            CheckCoverage(notify: false);
            s_coverageTimer.Start();
        }
    }

    private static void CheckCoverage(bool notify)
    {
        bool covered;
        try
        {
            covered = WindowCoverage.IsCovered(WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        }
        catch (Exception ex)
        {
            s_logger?.LogDebug(ex, "Could not check whether the main window is covered");
            covered = false;
        }

        if (covered == IsCovered)
        {
            return;
        }

        IsCovered = covered;
        s_logger?.LogDebug(covered ? "Main window covered by other windows: animations paused" : "Main window uncovered: animations resume");
        if (notify)
        {
            s_coverageChanged?.Invoke(null, EventArgs.Empty);
        }
    }
}
