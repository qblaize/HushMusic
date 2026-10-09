using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using HushMusic.App.Hosting;
using HushMusic.App.Services.Performance;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;

namespace HushMusic.App;

public partial class App : Application
{
    private IHost? _host;
    private ILogger<App>? _logger;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static MainWindow? MainWindow { get; private set; }

    public static T GetService<T>()
        where T : notnull => Services.GetRequiredService<T>();

    /// <summary>Another launch was redirected here: show (also from the notification area), restore and focus the main window. Safe from any thread.</summary>
    internal static void BringToFront()
    {
        if (MainWindow is null)
        {
            return;
        }

        try
        {
            Services?.GetService<IWindowModeService>()?.ShowMainWindow();
        }
        catch (ObjectDisposedException)
        {
            // Shutting down.
        }
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        var startup = StartupTiming.Begin();
        _host = AppHost.Build(DispatcherQueue.GetForCurrentThread());
        Services = _host.Services;
        _logger = Services.GetRequiredService<ILogger<App>>();
        _logger.LogInformation("HushMusic starting, version {Version}", typeof(App).Assembly.GetName().Version);
        startup.Mark("host");

        var settings = Services.GetRequiredService<ISettingsService>();
        await settings.LoadAsync();
        AppHost.ApplyLogLevel(Services, settings.Current.LogLevel);
        ApplyDesignSystem(settings.Current.DesignSystem);
        startup.Mark("settings");

        // The player sets up Media Foundation (two MediaPlayers and the system media controls), which takes a while:
        // build it on a worker thread while the window is created. The window's services wait for that same instance.
        _ = Task.Run(() => Services.GetRequiredService<IPlayer>());

        // Restore the saved session before any page loads, or the first Home feed is fetched signed out.
        // Local only (file read + DPAPI), so it doesn't delay the window noticeably.
        try
        {
            await Services.GetRequiredService<IAuthService>().InitializeAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the saved session");
        }

        startup.Mark("session");
        MainWindow = new MainWindow();
        startup.Mark("window");
        startup.LogFirstFrame(_logger);
        MainWindow.Closed += OnMainWindowClosed;
        if (AutoStartCommand.IsBackgroundLaunch(Environment.GetCommandLineArgs()))
        {
            // Started with Windows: hidden in the notification area (or minimized when close-to-tray is off).
            MainWindow.Modes.StartInBackground();
        }
        else if (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1")
        {
            // Automated test instances: shown behind every other window and never focused.
            TestWindowPlacement.ShowInBackground(MainWindow);
            TestWindowPlacement.RunTestHooks(MainWindow);
        }
        else
        {
            MainWindow.Activate();
        }

        try
        {
            await _host.StartAsync();
            startup.Mark("features");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A background feature failed to start");
            Services.GetRequiredService<INotificationService>().ShowError("A background feature failed to start", ex);
        }
    }

    // The design can't change while running: its resources must be in place before the first window or page loads.
    private void ApplyDesignSystem(string? setting)
    {
        DesignSystems.Active = DesignSystems.Normalize(setting);
        if (!DesignSystems.IsWindowsActive)
        {
            return;
        }

        try
        {
            var dropped = DesignSystems.UseWindowsResources(Resources);
            _logger?.LogInformation("Design: Windows ({Dropped} stock overrides of the Hush theme dropped)", dropped);
        }
        catch (Exception ex)
        {
            DesignSystems.Active = DesignSystems.Hush;
            _logger?.LogError(ex, "Could not load the Windows design");
        }
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _logger?.LogInformation("HushMusic shutting down");
        var host = _host;
        if (host is null)
        {
            return;
        }

        // Stop off the UI thread so hosted services cannot deadlock on the dispatcher; then flush logs.
        Task.Run(() => host.StopAsync(TimeSpan.FromSeconds(3))).Wait(TimeSpan.FromSeconds(4));
        host.Dispose();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unhandled UI exception");
        e.Handled = true;
        TryNotify(e.Exception);
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _logger?.LogError(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e) =>
        _logger?.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception");

    private static void TryNotify(Exception exception)
    {
        try
        {
            Services?.GetService<INotificationService>()?.ShowError("Something went wrong", exception);
        }
        catch
        {
            // Never let error reporting crash the app.
        }
    }
}
