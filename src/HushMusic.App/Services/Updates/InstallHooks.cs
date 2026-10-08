using Microsoft.Win32;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Updates;

/// <summary>
/// Velopack hooks (registered in Program.Main). Setup and Update.exe start the app with a hook argument; the hook
/// runs and the process exits before any other startup code.
/// </summary>
internal static class InstallHooks
{
    // The value AutoStartRegistration writes.
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "HushMusic";

    /// <summary>
    /// Removes the "Start with Windows" entry of this install. Everything in %LOCALAPPDATA%\HushMusic (settings,
    /// session, cache, yt-dlp) is kept on purpose: a reinstall picks up where the user left off.
    /// </summary>
    public static void BeforeUninstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            var executable = Environment.ProcessPath;
            if (key?.GetValue(RunValueName) is string command
                && executable is not null
                && AutoStartCommand.Reconcile(enabled: false, command, executable) == AutoStartAction.Delete)
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Never fail the uninstall over this.
        }
    }
}
