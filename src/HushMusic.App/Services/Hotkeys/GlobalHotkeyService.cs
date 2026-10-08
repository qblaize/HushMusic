using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.App.Services.Windowing;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Hotkeys;

/// <summary>Whether an action's global shortcut works.</summary>
public enum HotkeyState
{
    /// <summary>Global shortcuts are off, or the action has no shortcut.</summary>
    Off,
    Registered,

    /// <summary>Another app registered the same keys first.</summary>
    InUse,

    /// <summary>An earlier action uses the same keys (<see cref="HotkeyStatus.ConflictsWith"/>).</summary>
    Conflict,

    /// <summary>Windows refused it for another reason.</summary>
    Failed,
}

public sealed record HotkeyStatus(HotkeyAction Action, HotkeyGesture? Gesture, HotkeyState State, HotkeyAction? ConflictsWith = null);

/// <summary>
/// System-wide keyboard shortcuts (Settings → Global shortcuts), registered with RegisterHotKey while
/// <c>AppSettings.GlobalHotkeysEnabled</c> is on and applied live when the settings change.
/// </summary>
public interface IGlobalHotkeyService
{
    /// <summary>Every action's state as of the last change.</summary>
    IReadOnlyDictionary<HotkeyAction, HotkeyStatus> Status { get; }

    /// <summary>Raised on the UI thread when <see cref="Status"/> changes.</summary>
    event EventHandler? StatusChanged;

    /// <summary>
    /// Releases every shortcut until the result is disposed, so a key combination being recorded in Settings reaches
    /// the app instead of running its action. UI thread.
    /// </summary>
    IDisposable Pause();
}

/// <summary>
/// Owns a message-only window on the UI thread that receives WM_HOTKEY, and runs the actions through the player bar's
/// view model (the same commands as its buttons, so errors and saved volume behave alike).
/// </summary>
public sealed class GlobalHotkeyService : IGlobalHotkeyService, IDisposable
{
    private const string WindowClassName = "HushMusic.Hotkeys";
    private const uint ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly INotificationService _notifications;
    private readonly INavigationService _navigation;
    private readonly PlayerViewModel _player;
    private readonly ILogger<GlobalHotkeyService> _logger;
    private readonly bool _testInstance = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_BACKGROUND") == "1";
    private readonly Dictionary<HotkeyAction, HotkeyGesture> _registered = [];

    // Kept in a field: the window class holds only a function pointer to this delegate.
    private readonly Win32.WndProc _wndProc;
    private IntPtr _window;
    private Action? _showHide;
    private string? _applied;
    private int _pauses;
    private bool _attached;
    private bool _disposed;

    public GlobalHotkeyService(
        ISettingsService settings,
        IUiDispatcher dispatcher,
        INotificationService notifications,
        INavigationService navigation,
        PlayerViewModel player,
        ILogger<GlobalHotkeyService> logger)
    {
        _settings = settings;
        _dispatcher = dispatcher;
        _notifications = notifications;
        _navigation = navigation;
        _player = player;
        _logger = logger;
        _wndProc = WindowProc;
        Status = Off(null);
    }

    public IReadOnlyDictionary<HotkeyAction, HotkeyStatus> Status { get; private set; }

    public event EventHandler? StatusChanged;

    /// <summary>
    /// Test instances (HUSHMUSIC_TEST_BACKGROUND=1) register shortcuts only with HUSHMUSIC_TEST_HOTKEYS=1, and then only
    /// the ones saved in their settings (never the defaults), so an automated run can't take the user's shortcuts.
    /// </summary>
    private bool Allowed => !_testInstance || Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_HOTKEYS") == "1";

    /// <summary>Starts following the settings. UI thread, once, after the main window exists.</summary>
    internal void Attach(Action showHide)
    {
        if (_attached || _disposed)
        {
            return;
        }

        _attached = true;
        _showHide = showHide;
        _settings.Changed += OnSettingsChanged;
        Apply(reportInUse: true, force: true);
    }

    public IDisposable Pause()
    {
        _pauses++;
        Apply(reportInUse: false, force: true);
        return new Resumer(this);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_attached)
        {
            _settings.Changed -= OnSettingsChanged;
        }

        UnregisterAll();
        if (_window != IntPtr.Zero)
        {
            Win32.DestroyWindow(_window);
            Win32.UnregisterClass(WindowClassName, Win32.GetModuleHandle(null));
            _window = IntPtr.Zero;
        }
    }

    private static Dictionary<HotkeyAction, HotkeyStatus> Off(IReadOnlyDictionary<HotkeyAction, HotkeyGesture>? gestures) =>
        GlobalHotkeyMap.Actions.ToDictionary(
            a => a,
            a => new HotkeyStatus(a, gestures is not null && gestures.TryGetValue(a, out var gesture) ? gesture : null, HotkeyState.Off));

    private void OnSettingsChanged(object? sender, EventArgs e) => _dispatcher.Run(() => Apply(reportInUse: false, force: false));

    // Registers what the settings ask for, changing only the shortcuts that differ from what is registered. Other
    // settings changes (volume, theme…) leave everything as it is, including keys another app holds: those are tried
    // again when the shortcuts change, after recording, or at the next start.
    private void Apply(bool reportInUse, bool force)
    {
        if (_disposed || !_attached)
        {
            return;
        }

        var current = _settings.Current;
        var enabled = current.GlobalHotkeysEnabled && Allowed;
        var gestures = GlobalHotkeyMap.ResolveAll(current.GlobalHotkeys, useDefaults: !_testInstance);
        var applied = $"{enabled}|{string.Join(';', gestures.Select(g => $"{g.Key}={g.Value}"))}";
        if (!force && applied == _applied)
        {
            return;
        }

        _applied = applied;
        var conflicts = GlobalHotkeyMap.Conflicts(gestures);
        var wanted = enabled && _pauses == 0
            ? gestures.Where(g => !conflicts.ContainsKey(g.Key)).ToDictionary(g => g.Key, g => g.Value)
            : [];

        // Release first, so two actions can swap keys.
        foreach (var (action, gesture) in _registered.ToList())
        {
            if (!wanted.TryGetValue(action, out var keep) || keep != gesture)
            {
                Win32.UnregisterHotKey(_window, Id(action));
                _registered.Remove(action);
                _logger.LogDebug("Global shortcut {Gesture} ({Action}) released", gesture, action);
            }
        }

        var failures = new Dictionary<HotkeyAction, HotkeyState>();
        foreach (var (action, gesture) in wanted)
        {
            if (_registered.ContainsKey(action))
            {
                continue;
            }

            if (Register(action, gesture) is { } failure)
            {
                failures[action] = failure;
            }
        }

        // While paused (recording a shortcut) the rows keep showing the last real state.
        if (_pauses > 0)
        {
            return;
        }

        var previous = Status;
        Status = !enabled
            ? Off(gestures)
            : GlobalHotkeyMap.Actions.ToDictionary(a => a, a => Describe(a, gestures, conflicts, failures));
        if (!Status.SequenceEqual(previous))
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }

        if (reportInUse)
        {
            ReportInUse();
        }
    }

    private HotkeyState? Register(HotkeyAction action, HotkeyGesture gesture)
    {
        if (!EnsureWindow())
        {
            return HotkeyState.Failed;
        }

        // Volume keys repeat while held; the rest fire once per press.
        var modifiers = (uint)gesture.Modifiers | (action is HotkeyAction.VolumeUp or HotkeyAction.VolumeDown ? 0 : ModNoRepeat);
        if (Win32.RegisterHotKey(_window, Id(action), modifiers, (uint)gesture.Key))
        {
            _registered[action] = gesture;
            _logger.LogInformation("Global shortcut {Gesture} ({Action}) registered", gesture, action);
            return null;
        }

        var error = Marshal.GetLastPInvokeError();
        _logger.LogWarning("Global shortcut {Gesture} ({Action}) could not be registered (error {Error})", gesture, action, error);
        return error == ErrorHotkeyAlreadyRegistered ? HotkeyState.InUse : HotkeyState.Failed;
    }

    private static HotkeyStatus Describe(
        HotkeyAction action,
        IReadOnlyDictionary<HotkeyAction, HotkeyGesture> gestures,
        IReadOnlyDictionary<HotkeyAction, HotkeyAction> conflicts,
        IReadOnlyDictionary<HotkeyAction, HotkeyState> failures)
    {
        if (!gestures.TryGetValue(action, out var gesture))
        {
            return new HotkeyStatus(action, null, HotkeyState.Off);
        }

        if (conflicts.TryGetValue(action, out var owner))
        {
            return new HotkeyStatus(action, gesture, HotkeyState.Conflict, owner);
        }

        return new HotkeyStatus(action, gesture, failures.GetValueOrDefault(action, HotkeyState.Registered));
    }

    // At startup the user isn't looking at Settings, so a shortcut another app took is worth a message (one that stays
    // until it is read: the app may have started in the notification area).
    private void ReportInUse()
    {
        var taken = Status.Values.Where(s => s.State == HotkeyState.InUse && s.Gesture is not null).Select(s => s.Gesture!.Value.ToString()).ToList();
        if (taken.Count == 0)
        {
            return;
        }

        var keys = taken.Count == 1 ? taken[0] : string.Join(", ", taken.Take(taken.Count - 1)) + " and " + taken[^1];
        _notifications.Show(new AppNotification(
            NotificationSeverity.Warning,
            taken.Count == 1 ? "A global shortcut is taken" : "Some global shortcuts are taken",
            $"Another app already uses {keys}. Choose different keys in Settings → Global shortcuts.")
        {
            Action = new NotificationAction("Open Settings", () => _navigation.NavigateTo(PageKey.Settings)),
        });
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered.Keys)
        {
            Win32.UnregisterHotKey(_window, Id(action));
        }

        _registered.Clear();
    }

    private static int Id(HotkeyAction action) => (int)action + 1;

    private bool EnsureWindow()
    {
        if (_window != IntPtr.Zero)
        {
            return true;
        }

        var instance = Win32.GetModuleHandle(null);
        var windowClass = new Win32.WndClassEx
        {
            Size = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
            WndProc = _wndProc,
            Instance = instance,
            ClassName = WindowClassName,
        };

        Win32.RegisterClassEx(ref windowClass);
        _window = Win32.CreateWindowEx(0, WindowClassName, "Hush shortcuts", 0, 0, 0, 0, 0, Win32.HwndMessage, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            _logger.LogWarning("Could not create the global shortcut window (error {Error})", Marshal.GetLastPInvokeError());
            return false;
        }

        return true;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == Win32.WmHotKey)
            {
                var id = (int)(long)wParam;
                if (id >= 1 && id <= GlobalHotkeyMap.Actions.Count)
                {
                    Run((HotkeyAction)(id - 1));
                }

                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // An exception must never unwind into the native window procedure.
            _logger.LogError(ex, "Global shortcut message {Message:X} failed", msg);
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Run(HotkeyAction action)
    {
        _logger.LogDebug("Global shortcut: {Action}", action);
        switch (action)
        {
            case HotkeyAction.PlayPause:
                _player.PlayPauseCommand.Execute(null);
                break;
            case HotkeyAction.Next:
                _player.NextCommand.Execute(null);
                break;
            case HotkeyAction.Previous:
                _player.PreviousCommand.Execute(null);
                break;
            case HotkeyAction.VolumeUp:
                _player.Volume = TaskbarWidgetLayout.StepVolume(_player.Volume, 1);
                break;
            case HotkeyAction.VolumeDown:
                _player.Volume = TaskbarWidgetLayout.StepVolume(_player.Volume, -1);
                break;
            case HotkeyAction.Mute:
                _player.ToggleMuteCommand.Execute(null);
                break;
            case HotkeyAction.Like when _player.CanRate:
                _player.ToggleLikeCommand.Execute(null);
                break;
            case HotkeyAction.ShowHide:
                _showHide?.Invoke();
                break;
        }
    }

    private void Resume()
    {
        if (_pauses > 0)
        {
            _pauses--;
            Apply(reportInUse: false, force: true);
        }
    }

    private sealed class Resumer(GlobalHotkeyService owner) : IDisposable
    {
        private GlobalHotkeyService? _owner = owner;

        public void Dispose()
        {
            _owner?.Resume();
            _owner = null;
        }
    }
}
