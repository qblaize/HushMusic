using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Velopack;
using HushMusic.App.Services.Notifications;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Updates;
using HushMusic.Core.Services;

namespace HushMusic.App;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so only one copy of the app runs. A second launch hands its
/// activation to the running copy, which comes to the front, and exits: two copies would play over each other and
/// fight over the settings file, the log and the media keys.
/// </summary>
public static class Program
{
    // One instance per install folder: a launch of the same build redirects, a build in another folder
    // (e.g. a test build next to the copy in use) runs on its own.
    private static string InstanceKey => "HushMusic." + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(AppContext.BaseDirectory.ToUpperInvariant())))[..16];

    [STAThread]
    private static int Main(string[] args)
    {
        // First: Setup and the updater start the app to run an install/update/uninstall hook, which must not be
        // redirected to a running copy (the hook exits the process here). A downloaded update is not installed on
        // launch, because a second launch would have to stop the copy that is playing: IUpdateService asks for a restart.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUninstallFastCallback(_ =>
            {
                InstallHooks.BeforeUninstall();
                TrackNotificationService.RemoveRegistration();
            })
            .Run();

        XamlCheckProcessRequirements();
        WinRT.ComWrappersSupport.InitializeComWrappers();

        // A restart (e.g. for a new design) starts this copy while the old one is still shutting down: let it finish
        // before this one becomes the running copy.
        DesignSystems.WaitForPreviousInstance(args);

        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (!mainInstance.IsCurrent)
        {
            RedirectTo(mainInstance);
            return 0;
        }

        mainInstance.Activated += (_, e) =>
        {
            // A click on a song notification that started a second copy: the notification decides (Next, or show).
            if (e.Kind == ExtendedActivationKind.AppNotification)
            {
                OnNotificationActivation(e);
                return;
            }

            // The Run entry ("--background") starting while the app already runs changes nothing.
            if (!IsBackgroundActivation(e))
            {
                App.BringToFront();
            }
        };
        Application.Start(callbackParams =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    // The redirect must finish before this process exits, and this STA thread has to keep pumping COM while it
    // waits, hence a worker task plus CoWaitForMultipleObjects (the pattern from the Windows App SDK docs).
    private static void RedirectTo(AppInstance mainInstance)
    {
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // A start with Windows, or an automated test, never brings the running copy to the front.
        var background = AutoStartCommand.IsBackgroundLaunch(Environment.GetCommandLineArgs())
            || Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";

        // A notification click leaves it to the running copy, which comes forward only when the click asks for it
        // (not for the Next button).
        var notification = activation.Kind == ExtendedActivationKind.AppNotification;
        if (!background)
        {
            // This process was started by the user, so it may let the running copy take the foreground (its window
            // may be hidden in the notification area, where only the running copy can show it).
            AllowSetForegroundWindow(mainInstance.ProcessId);
        }

        using var redirected = new ManualResetEvent(false);
        _ = Task.Run(() =>
        {
            try
            {
                mainInstance.RedirectActivationToAsync(activation).AsTask().Wait();
            }
            finally
            {
                redirected.Set();
            }
        });
        _ = CoWaitForMultipleObjects(0, 10_000, 1, [redirected.SafeWaitHandle.DangerousGetHandle()], out _);
        if (background || notification)
        {
            return;
        }

        // This process was started by the user, so it may give the foreground to the running copy's window.
        try
        {
            using var running = Process.GetProcessById((int)mainInstance.ProcessId);
            SetForegroundWindow(running.MainWindowHandle);
        }
        catch (ArgumentException)
        {
            // The running copy exited meanwhile.
        }
    }

    private static void OnNotificationActivation(AppActivationArguments activation)
    {
        try
        {
            var argument = (activation.Data as AppNotificationActivatedEventArgs)?.Argument;
            App.Services?.GetService<TrackNotificationService>()?.OnRedirectedActivation(argument);
        }
        catch (Exception)
        {
            // Starting up or shutting down: nothing to do with the click.
        }
    }

    private static bool IsBackgroundActivation(AppActivationArguments activation)
    {
        try
        {
            return activation.Kind == ExtendedActivationKind.Launch
                && activation.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch
                && AutoStartCommand.IsBackgroundLaunch(launch.Arguments);
        }
        catch (Exception)
        {
            return false;
        }
    }

    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(uint dwFlags, uint dwMilliseconds, uint nHandles, IntPtr[] pHandles, out uint dwIndex);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
