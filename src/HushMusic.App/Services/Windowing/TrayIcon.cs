using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HushMusic.App.Services.Windowing;

/// <summary>Notification-area menu commands (also the WM_COMMAND ids posted to the icon's window).</summary>
public enum TrayCommand
{
    PlayPause = 1,
    Next = 2,
    Previous = 3,
    Show = 4,
    Quit = 5,
}

/// <summary>What the notification-area menu shows when it opens.</summary>
internal readonly record struct TrayMenuState(bool HasTrack, bool IsPlaying, bool DarkMenu);

/// <summary>
/// Notification-area icon (Shell_NotifyIcon) with a native popup menu. It is owned by a hidden top-level window of
/// its own, so opening the menu (which needs a foreground window) never raises the main window, and that window
/// receives Explorer's "TaskbarCreated" broadcast to re-add the icon after Explorer restarts. UI thread only.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    /// <summary>Class of the hidden owner window (one per process).</summary>
    public const string WindowClassName = "HushMusic.NotifyIcon";

    private const uint CallbackMessage = Win32.WmApp + 1;
    private const uint IconId = 1;
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;
    private const uint NifMessage = 0x1;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifShowTip = 0x80;
    private const uint NotifyIconVersion4 = 4;
    private const int NinSelect = 0x400;
    private const int NinKeySelect = 0x401;
    private const uint MfString = 0x0;
    private const uint MfGrayed = 0x1;
    private const uint MfSeparator = 0x800;
    private const uint TpmRightButton = 0x2;
    private const uint TpmBottomAlign = 0x20;
    private const uint WsPopup = 0x80000000;
    private const uint WsExToolWindow = 0x80;
    private const int TipLength = 127;
    private static readonly long ToggleDebounceTicks = TimeSpan.FromMilliseconds(400).Ticks;

    private readonly Func<bool, string> _iconPath;
    private readonly Func<TrayMenuState> _menuState;
    private readonly Action<TrayCommand> _onCommand;
    private readonly Action _onToggle;
    private readonly ILogger _logger;
    private readonly uint _taskbarCreatedMessage;

    // Kept in a field: the window class holds only a function pointer to this delegate.
    private readonly Win32.WndProc _wndProc;
    private IntPtr _window;
    private IntPtr _icon;
    private bool? _iconForLightTaskbar;
    private bool _wanted;
    private bool _added;
    private string _tooltip = "Hush";
    private long _lastToggleTicks;
    private bool _disposed;

    /// <param name="iconPath">The icon file for a light (true) or dark (false) taskbar.</param>
    public TrayIcon(Func<bool, string> iconPath, Func<TrayMenuState> menuState, Action<TrayCommand> onCommand, Action onToggle, ILogger logger)
    {
        _iconPath = iconPath;
        _menuState = menuState;
        _onCommand = onCommand;
        _onToggle = onToggle;
        _logger = logger;
        _wndProc = WindowProc;
        _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");
    }

    public bool IsVisible => _added;

    /// <summary>Adds or removes the icon.</summary>
    public void SetVisible(bool visible)
    {
        if (_disposed || visible == _wanted)
        {
            return;
        }

        _wanted = visible;
        if (visible)
        {
            Add();
        }
        else
        {
            Remove();
        }
    }

    /// <summary>Hover text; trimmed to what the shell shows (127 characters).</summary>
    public void SetTooltip(string text)
    {
        var tip = text.Length > TipLength ? string.Concat(text.AsSpan(0, TipLength - 1), "…") : text;
        if (tip == _tooltip)
        {
            return;
        }

        _tooltip = tip;
        if (_added)
        {
            var data = NewData(NifTip | NifShowTip);
            Win32.Shell_NotifyIcon(NimModify, ref data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Remove();
        if (_window != IntPtr.Zero)
        {
            Win32.DestroyWindow(_window);
            Win32.UnregisterClass(WindowClassName, Win32.GetModuleHandle(null));
            _window = IntPtr.Zero;
        }

        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
            _icon = IntPtr.Zero;
        }
    }

    private void Add()
    {
        if (!EnsureWindow())
        {
            return;
        }

        EnsureIcon();

        var data = NewData(NifMessage | NifIcon | NifTip | NifShowTip);
        var added = Win32.Shell_NotifyIcon(NimAdd, ref data) || Win32.Shell_NotifyIcon(NimModify, ref data);
        if (added)
        {
            // Version 4: NIN_SELECT / WM_CONTEXTMENU with the anchor point in wParam.
            data.Version = NotifyIconVersion4;
            Win32.Shell_NotifyIcon(NimSetVersion, ref data);
        }

        _added = added;
        _logger.Log(added ? LogLevel.Information : LogLevel.Warning, "Notification-area icon {Result}", added ? "added" : "could not be added");
    }

    private void Remove()
    {
        if (!_added || _window == IntPtr.Zero)
        {
            return;
        }

        var data = NewData(0);
        Win32.Shell_NotifyIcon(NimDelete, ref data);
        _added = false;
        _logger.LogInformation("Notification-area icon removed");
    }

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
        _window = Win32.CreateWindowEx(WsExToolWindow, WindowClassName, "Hush", WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (_window == IntPtr.Zero)
        {
            _logger.LogWarning("Could not create the notification-area window (error {Error})", Marshal.GetLastPInvokeError());
            return false;
        }

        return true;
    }

    private Win32.NotifyIconData NewData(uint flags) => new()
    {
        Size = (uint)Marshal.SizeOf<Win32.NotifyIconData>(),
        Window = _window,
        Id = IconId,
        Flags = flags,
        CallbackMessage = CallbackMessage,
        Icon = _icon,
        Tip = _tooltip,
        Info = string.Empty,
        InfoTitle = string.Empty,
    };

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                OnIconEvent(Win32.LowWord(lParam), wParam);
                return IntPtr.Zero;
            }

            if (msg == _taskbarCreatedMessage && msg != 0)
            {
                // Explorer restarted: every icon is gone.
                _added = false;
                if (_wanted)
                {
                    Add();
                }

                return IntPtr.Zero;
            }

            // The taskbar switched between light and dark: swap to the matching monochrome icon.
            if (msg == Win32.WmSettingChange && _added && Win32.LightTaskbar() != _iconForLightTaskbar)
            {
                EnsureIcon();
                var data = NewData(NifIcon);
                Win32.Shell_NotifyIcon(NimModify, ref data);
                return IntPtr.Zero;
            }

            // Menu choices arrive here as WM_COMMAND (TrackPopupMenuEx without TPM_RETURNCMD).
            if (msg == Win32.WmCommand && Enum.IsDefined((TrayCommand)Win32.LowWord(wParam)))
            {
                _onCommand((TrayCommand)Win32.LowWord(wParam));
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // An exception must never unwind into the native window procedure.
            _logger.LogError(ex, "Notification-area message {Message:X} failed", msg);
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void EnsureIcon()
    {
        var light = Win32.LightTaskbar();
        if (_icon != IntPtr.Zero && light == _iconForLightTaskbar)
        {
            return;
        }

        if (_icon != IntPtr.Zero)
        {
            Win32.DestroyIcon(_icon);
        }

        _icon = Win32.LoadSmallIcon(_iconPath(light), Win32.GetDpiForSystem());
        _iconForLightTaskbar = light;
    }

    private void OnIconEvent(int iconEvent, IntPtr anchor)
    {
        switch (iconEvent)
        {
            case NinSelect or NinKeySelect:
                // Enter can arrive twice, and a double click selects twice.
                var now = DateTime.UtcNow.Ticks;
                if (now - _lastToggleTicks > ToggleDebounceTicks)
                {
                    _lastToggleTicks = now;
                    _onToggle();
                }

                break;
            case (int)Win32.WmContextMenu:
                ShowMenu(Win32.SignedLowWord(anchor), Win32.SignedHighWord(anchor));
                break;
        }
    }

    private void ShowMenu(int x, int y)
    {
        var state = _menuState();
        ApplyMenuTheme(state.DarkMenu);
        var menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var transport = state.HasTrack ? MfString : MfGrayed;
            Append(menu, transport, TrayCommand.PlayPause, state.IsPlaying ? "Pause" : "Play");
            Append(menu, transport, TrayCommand.Next, "Next");
            Append(menu, transport, TrayCommand.Previous, "Previous");
            Win32.AppendMenu(menu, MfSeparator, UIntPtr.Zero, null);
            Append(menu, MfString, TrayCommand.Show, "Show Hush");
            Append(menu, MfString, TrayCommand.Quit, "Quit");
            Win32.SetMenuDefaultItem(menu, (uint)TrayCommand.Show, 0);

            // The menu only closes on an outside click if its owner is the foreground window (KB135788).
            Win32.SetForegroundWindow(_window);
            Win32.TrackPopupMenuEx(menu, TpmRightButton | TpmBottomAlign, x, y, _window, IntPtr.Zero);
            Win32.PostMessage(_window, Win32.WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }
    }

    private static void Append(IntPtr menu, uint flags, TrayCommand command, string text) =>
        Win32.AppendMenu(menu, flags, (UIntPtr)(uint)command, text);

    // Win32 popup menus only follow dark mode through uxtheme's unnamed SetPreferredAppMode (ordinal 135, Windows 10
    // 1903+), which Explorer and Windows Terminal use too. Best effort: on failure the menu is simply light.
    internal static void ApplyMenuTheme(bool dark)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            return;
        }

        try
        {
            SetPreferredAppMode(dark ? 2 /* ForceDark */ : 3 /* ForceLight */);
            FlushMenuThemes();
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }
    }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(int mode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();
}
