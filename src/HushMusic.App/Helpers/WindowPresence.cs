using Microsoft.Extensions.DependencyInjection;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Helpers;

/// <summary>
/// Whether the main window is on screen, for UI that should let go of what it shows (artwork, animations) while
/// nobody can see it: minimized, or hidden in the notification area. A UI-thread view of
/// <see cref="IWindowModeService.WindowVisibility"/>; the mini player counts as shown.
/// </summary>
internal static class WindowPresence
{
    private static IWindowModeService? s_windowMode;
    private static bool s_resolved;
    private static EventHandler? s_changed;

    /// <summary>True while the main window (or the mini player) is on screen.</summary>
    public static bool IsShown => WindowMode()?.IsWindowVisible ?? true;

    /// <summary>
    /// The main window is minimized or hidden and the element is in it (or not in any window yet: it is checked again
    /// when it loads). Elements of other windows (the taskbar player's flyout) don't count: they come and go with their
    /// own window.
    /// </summary>
    public static bool Hides(UIElement element) =>
        !IsShown && (element.XamlRoot is not { } root || root == App.MainWindow?.Content?.XamlRoot);

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

    private static IWindowModeService? WindowMode()
    {
        if (!s_resolved && App.Services is { } services)
        {
            s_resolved = true;
            s_windowMode = services.GetService<IWindowModeService>();
            if (s_windowMode is not null)
            {
                s_windowMode.VisibilityChanged += (_, _) => s_changed?.Invoke(null, EventArgs.Empty);
            }
        }

        return s_windowMode;
    }
}
