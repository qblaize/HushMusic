using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using HushMusic.App.Hosting;
using HushMusic.App.Services.Hotkeys;
using HushMusic.App.Services.Notifications;
using HushMusic.App.Services.Performance;
using HushMusic.App.Services.Windowing;
using HushMusic.App.Services.Windowing.TaskbarWidget;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Shell;

/// <summary>Whether the user can see the main window.</summary>
public enum MainWindowVisibility
{
    /// <summary>On screen, as the normal window or the mini player (possibly covered by other windows).</summary>
    Shown,

    /// <summary>Minimized to the taskbar.</summary>
    Minimized,

    /// <summary>Hidden in the notification area: closed to it, or started with Windows.</summary>
    Hidden,
}

/// <summary>Main window states: normal, mini player (always-on-top compact overlay), hidden in the notification area.</summary>
public interface IWindowModeService
{
    bool IsMiniPlayer { get; }

    /// <summary>Raised on the UI thread when <see cref="IsMiniPlayer"/> changes.</summary>
    event EventHandler? Changed;

    /// <summary>Shown, minimized or hidden in the notification area. Read it on the UI thread.</summary>
    MainWindowVisibility WindowVisibility { get; }

    /// <summary>True while the main window (or the mini player) is on screen: neither minimized nor hidden.</summary>
    bool IsWindowVisible { get; }

    /// <summary>
    /// Raised on the UI thread when <see cref="WindowVisibility"/> changes, after the window has been shown, minimized,
    /// restored or hidden. A few seconds after the window hides in the notification area (a minute after it is
    /// minimized), the process gives its unused memory back to Windows (<see cref="ProcessMemory.TrimSoon"/>), so UI
    /// released here is returned too.
    /// </summary>
    event EventHandler? VisibilityChanged;

    void EnterMiniPlayer();

    void ExitMiniPlayer();

    /// <summary>Shows, restores and focuses the main window (tray, second launch, mini player). Safe from any thread.</summary>
    void ShowMainWindow();
}

/// <summary>
/// Runs the main window's modes and its Windows integration: the mini player (CompactOverlay presenter, its own
/// in-session size and position), close to the notification area, the tray icon and menu, taskbar thumbnail buttons
/// and progress, the taskbar player, global shortcuts, song notifications and the start-with-Windows entry.
/// <see cref="MainWindow"/> attaches itself on creation.
/// </summary>
public sealed class WindowModeService : IWindowModeService, IDisposable
{
    private const int MiniSize = 340;
    private const int MiniMargin = 24;

    // Long enough for the UI to release what it showed (VisibilityChanged) and for a quick reopen to cancel it.
    private static readonly TimeSpan HiddenTrimDelay = TimeSpan.FromSeconds(5);

    // Minimized windows usually come back soon, and the pages a trim drops have to come back with them: only a window
    // that stays down is trimmed.
    private static readonly TimeSpan MinimizedTrimDelay = TimeSpan.FromSeconds(60);

    // Started hidden with Windows: the first pages and the restored session load meanwhile.
    private static readonly TimeSpan BackgroundStartTrimDelay = TimeSpan.FromSeconds(30);

    private readonly IPlayer _player;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly IThemeService _theme;
    private readonly TaskbarPlayerService _taskbarPlayer;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly TrackNotificationService _trackNotifications;
    private readonly ILogger<WindowModeService> _logger;
    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";

    private MainWindow? _window;
    private IntPtr _hwnd;
    private PlaybackRemote? _remote;
    private TaskbarButtons? _taskbar;
    private WindowMessageHook? _hook;
    private TrayIcon? _tray;
    private AutoStartRegistration? _autoStart;

    private AppWindowPresenter? _presenterBeforeMini;
    private Win32.WindowPlacement _placementBeforeMini;
    private TitleBarHeightOption _captionBeforeMini;
    private RectInt32? _miniBounds;
    private bool _quitting;
    private bool _detached;
    private bool _visibilityCheckQueued;

    public WindowModeService(
        IPlayer player,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        INotificationService notifications,
        IThemeService theme,
        TaskbarPlayerService taskbarPlayer,
        GlobalHotkeyService hotkeys,
        TrackNotificationService trackNotifications,
        ILogger<WindowModeService> logger)
    {
        _player = player;
        _settings = settings;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _theme = theme;
        _taskbarPlayer = taskbarPlayer;
        _hotkeys = hotkeys;
        _trackNotifications = trackNotifications;
        _logger = logger;
    }

    public bool IsMiniPlayer { get; private set; }

    public event EventHandler? Changed;

    public MainWindowVisibility WindowVisibility { get; private set; } = MainWindowVisibility.Hidden;

    public bool IsWindowVisible => WindowVisibility == MainWindowVisibility.Shown;

    public event EventHandler? VisibilityChanged;

    // Monochrome wave: black on a light taskbar, white on a dark one.
    private static string TrayIconPath(bool lightTaskbar) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", lightTaskbar ? "TrayIconLight.ico" : "TrayIconDark.ico");

    public void EnterMiniPlayer() => _dispatcher.Run(EnterMini);

    public void ExitMiniPlayer() => _dispatcher.Run(ExitMini);

    public void ShowMainWindow() => _dispatcher.Run(() =>
    {
        if (_window is null || _detached)
        {
            return;
        }

        ExitMini();
        Present();
    });

    /// <summary>Called once by the main window's constructor (UI thread).</summary>
    internal void Attach(MainWindow window)
    {
        _window = window;
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        _remote = new PlaybackRemote(_player, _notifications);
        _taskbar = new TaskbarButtons(_hwnd, _player, _dispatcher, _remote, _logger);
        _hook = new WindowMessageHook(_hwnd, HandleMessage, _logger);
        WindowVisibility = ReadVisibility();

        // Killed test instances would leave ghost icons in the user's notification area.
        if (!_testInstance || Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_TRAY") == "1")
        {
            _tray = new TrayIcon(TrayIconPath, MenuState, OnTrayCommand, ToggleFromTray, _logger);
            _player.TrackChanged += OnTrackChanged;
        }

        _settings.Changed += OnSettingsChanged;
        _theme.ThemeChanged += OnThemeChanged;
        window.AppWindow.Closing += OnClosing;
        window.Closed += (_, _) => Detach();

        _autoStart = new AutoStartRegistration(_settings, _logger);
        _autoStart.Start();
        _taskbarPlayer.Attach(ShowMainWindow);
        _hotkeys.Attach(ToggleFromShortcut);
        _trackNotifications.Attach(ShowMainWindow, () => _remote?.Next());
        UpdateTray();
    }

    /// <summary>Started by the Run entry ("--background"): stay hidden in the notification area, or minimized without it.</summary>
    internal void StartInBackground()
    {
        if (_window is null)
        {
            return;
        }

        if (_settings.Current.CloseToTray && _tray is not null)
        {
            UpdateTray();
            ProcessMemory.TrimSoon(BackgroundStartTrimDelay, _logger);
            _logger.LogInformation("Started in the notification area");
            return;
        }

        Win32.ShowWindow(_hwnd, Win32.SwShowMinNoActive);
        _logger.LogInformation("Started minimized");
    }

    /// <summary>The mini player's close button: behaves like the window's own close button (WM_CLOSE).</summary>
    internal void CloseLikeUser()
    {
        if (_hwnd != IntPtr.Zero)
        {
            Win32.PostMessage(_hwnd, Win32.WmClose, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Really exits (the tray's Quit), through the normal window-closed shutdown.</summary>
    internal void Quit()
    {
        if (_window is null)
        {
            return;
        }

        _logger.LogInformation("Quit requested");
        _quitting = true;
        _window.Close();
    }

    public void Dispose() => Detach();

    private void EnterMini()
    {
        if (_window is null || IsMiniPlayer || _detached)
        {
            return;
        }

        var appWindow = _window.AppWindow;
        try
        {
            _presenterBeforeMini = appWindow.Presenter;
            _placementBeforeMini = new Win32.WindowPlacement { Length = (uint)Marshal.SizeOf<Win32.WindowPlacement>() };
            Win32.GetWindowPlacement(_hwnd, ref _placementBeforeMini);
            _captionBeforeMini = appWindow.TitleBar.PreferredHeightOption;

            // Swap first, so the shell is never laid out at mini size (its pages and scroll positions stay as they were).
            _window.ShowMiniPlayerContent(true);
            appWindow.SetPresenter(AppWindowPresenterKind.CompactOverlay);

            // No caption buttons over the artwork: the mini player draws its own close button, on hover.
            appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
            appWindow.MoveAndResize(MiniBounds(appWindow));
            if (_testInstance)
            {
                // Test instances stay behind the user's windows, even as an always-on-top window.
                TestWindowPlacement.SendToBack(_window);
            }

            IsMiniPlayer = true;
            _logger.LogInformation("Mini player on");
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not switch to the mini player");
            _notifications.ShowError("Could not open the mini player", ex);
            RestoreNormalWindow(appWindow);
            _window.ShowMiniPlayerContent(false);
        }
    }

    private void ExitMini()
    {
        if (_window is null || !IsMiniPlayer)
        {
            return;
        }

        var appWindow = _window.AppWindow;
        if (appWindow.Presenter.Kind == AppWindowPresenterKind.CompactOverlay)
        {
            _miniBounds = new RectInt32(appWindow.Position.X, appWindow.Position.Y, appWindow.Size.Width, appWindow.Size.Height);
        }

        try
        {
            RestoreNormalWindow(appWindow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not restore the window from the mini player");
        }

        _window.ShowMiniPlayerContent(false);
        if (_testInstance)
        {
            TestWindowPlacement.SendToBack(_window);
        }

        IsMiniPlayer = false;
        _logger.LogInformation("Mini player off");
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // The presenter, size, position and maximized state from before the mini player.
    private void RestoreNormalWindow(AppWindow appWindow)
    {
        appWindow.TitleBar.PreferredHeightOption = _captionBeforeMini;
        var previous = _presenterBeforeMini is { Kind: not AppWindowPresenterKind.CompactOverlay } presenter ? presenter : null;
        try
        {
            // The same presenter object keeps its settings (minimum size).
            if (previous is not null)
            {
                appWindow.SetPresenter(previous);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The previous presenter could not be reused");
            previous = null;
        }

        if (previous is null)
        {
            appWindow.SetPresenter(AppWindowPresenterKind.Overlapped);
            _window?.ConfigurePresenter();
        }

        if (appWindow.Presenter.Kind == AppWindowPresenterKind.Overlapped && _placementBeforeMini.Length != 0)
        {
            var placement = _placementBeforeMini;
            placement.Flags = 0;
            placement.ShowCmd = placement.ShowCmd == Win32.SwShowMaximized ? Win32.SwShowMaximized : Win32.SwShowNoActivate;
            Win32.SetWindowPlacement(_hwnd, ref placement);
        }
    }

    // Last mini position in this session if it is still on a display; else the bottom-right corner of this display.
    private RectInt32 MiniBounds(AppWindow appWindow)
    {
        if (_miniBounds is { } saved && DisplayArea.GetFromRect(saved, DisplayAreaFallback.None) is not null)
        {
            return saved;
        }

        var dpi = Win32.GetDpiForWindow(_hwnd);
        var scale = (dpi == 0 ? 96 : dpi) / 96.0;
        var size = (int)Math.Round(MiniSize * scale);
        var margin = (int)Math.Round(MiniMargin * scale);
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        return new RectInt32(area.X + area.Width - size - margin, area.Y + area.Height - size - margin, size, size);
    }

    // Shows (keeping the mini player if it is on), restores from minimized and brings the window to the front.
    private void Present()
    {
        if (_window is null)
        {
            return;
        }

        var appWindow = _window.AppWindow;
        if (_testInstance)
        {
            // Test instances never take the foreground, even when a test relaunches the same build.
            appWindow.Show(activateWindow: false);
            TestWindowPlacement.SendToBack(_window);
            UpdateTray();
            return;
        }

        if (!appWindow.IsVisible)
        {
            appWindow.Show(activateWindow: true);
        }

        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
        {
            presenter.Restore();
        }

        _window.Activate();
        Win32.SetForegroundWindow(_hwnd);
        UpdateTray();
    }

    private void HideToTray()
    {
        if (_window is null)
        {
            return;
        }

        _tray?.SetVisible(true);
        _window.AppWindow.Hide();
        _logger.LogInformation("Window hidden to the notification area");
    }

    private void ToggleFromTray()
    {
        if (_window is null)
        {
            return;
        }

        var appWindow = _window.AppWindow;
        var minimized = appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (appWindow.IsVisible && !minimized)
        {
            HideToTray();
        }
        else
        {
            Present();
        }
    }

    // The Show/hide global shortcut: brings Hush to the front, or puts it away when it already is in front (to the
    // notification area when the window closes to it, else minimized). The mini player stays the mini player.
    private void ToggleFromShortcut()
    {
        if (_window is null || _detached)
        {
            return;
        }

        var appWindow = _window.AppWindow;
        var minimized = appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        if (!appWindow.IsVisible || minimized || Win32.GetForegroundWindow() != _hwnd)
        {
            Present();
        }
        else if (_settings.Current.CloseToTray && _tray is not null)
        {
            HideToTray();
        }
        else
        {
            Win32.ShowWindow(_hwnd, Win32.SwMinimize);
        }
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.PlayPause:
                _remote?.PlayPause();
                break;
            case TrayCommand.Next:
                _remote?.Next();
                break;
            case TrayCommand.Previous:
                _remote?.Previous();
                break;
            case TrayCommand.Show:
                ShowMainWindow();
                break;
            case TrayCommand.Quit:
                Quit();
                break;
        }
    }

    private TrayMenuState MenuState() =>
        new(_remote?.HasTrack == true, _remote?.IsPlaying == true, _theme.ActualTheme != ElementTheme.Light);

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_quitting || !_settings.Current.CloseToTray)
        {
            return;
        }

        // Close to the notification area: the window goes, the music keeps playing.
        args.Cancel = true;
        HideToTray();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Run(UpdateTray);

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => _dispatcher.Run(UpdateTray);

    // ThemeService re-styles the caption buttons on theme changes; keep them hidden in the mini player.
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        if (IsMiniPlayer && _window is not null)
        {
            _window.AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        }
    }

    // Main-window messages: the taskbar's, plus the ones sent when the window is shown, hidden, minimized or restored.
    private bool HandleMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message is Win32.WmShowWindow or Win32.WmSize or Win32.WmWindowPosChanged)
        {
            QueueVisibilityCheck();
        }

        return _taskbar?.HandleMessage(message, wParam, lParam) == true;
    }

    // Checked once the messages of a change have all been handled (the window's state is final by then), and outside
    // the window procedure, so VisibilityChanged handlers may do real work.
    private void QueueVisibilityCheck()
    {
        if (!_visibilityCheckQueued && _window is not null)
        {
            _visibilityCheckQueued = _window.DispatcherQueue.TryEnqueue(CheckVisibility);
        }
    }

    private void CheckVisibility()
    {
        _visibilityCheckQueued = false;
        var visibility = ReadVisibility();
        if (_detached || visibility == WindowVisibility)
        {
            return;
        }

        WindowVisibility = visibility;
        _logger.LogDebug("Main window: {Visibility}", visibility);

        // With nothing on screen the process gives its free memory back to Windows, once the UI has let go of what it
        // showed (VisibilityChanged, below). Back on screen, a pending trim is dropped.
        switch (visibility)
        {
            case MainWindowVisibility.Hidden:
                ProcessMemory.TrimSoon(HiddenTrimDelay, _logger);
                break;
            case MainWindowVisibility.Minimized:
                ProcessMemory.TrimSoon(MinimizedTrimDelay, _logger);
                break;
            default:
                ProcessMemory.CancelTrim();
                break;
        }

        VisibilityChanged?.Invoke(this, EventArgs.Empty);
    }

    private MainWindowVisibility ReadVisibility() =>
        !Win32.IsWindowVisible(_hwnd) ? MainWindowVisibility.Hidden
        : Win32.IsIconic(_hwnd) ? MainWindowVisibility.Minimized
        : MainWindowVisibility.Shown;

    // The icon exists while close-to-tray is on or the window is hidden.
    private void UpdateTray()
    {
        if (_tray is null || _window is null || _detached)
        {
            return;
        }

        var track = _player.CurrentTrack;
        _tray.SetTooltip(track is null
            ? "Hush"
            : string.IsNullOrEmpty(track.ArtistsText) ? track.Title : $"{track.Title} — {track.ArtistsText}");
        _tray.SetVisible(_settings.Current.CloseToTray || !_window.AppWindow.IsVisible);
    }

    private void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        ProcessMemory.CancelTrim();
        _settings.Changed -= OnSettingsChanged;
        _theme.ThemeChanged -= OnThemeChanged;
        _player.TrackChanged -= OnTrackChanged;
        _autoStart?.Dispose();
        _taskbarPlayer.Dispose();
        _hotkeys.Dispose();
        _trackNotifications.Dispose();
        _tray?.Dispose();
        _hook?.Dispose();
        _taskbar?.Dispose();
    }
}
