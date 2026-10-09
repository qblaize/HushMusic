using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// The taskbar player: a layered window parented into a taskbar (the main Shell_TrayWnd, or the Shell_SecondaryTrayWnd
/// of another display), drawn with GDI+ and updated with UpdateLayeredWindow. Windows 11 has no API for this; it is the
/// technique tools like TrafficMonitor use. One instance per taskbar.
/// <para>
/// Everything runs on a dedicated thread with its own message loop. A child window whose parent belongs to another
/// thread shares that thread's input queue (the system attaches them), so this thread and Explorer's taskbar thread
/// wait on each other: if it ever stalled, the taskbar would freeze, and a busy WinUI thread would do just that. The
/// thread therefore only draws a snapshot (<see cref="Update"/>) and posts commands to an <see cref="ITaskbarWidgetSink"/>.
/// </para>
/// <para>
/// As a child of the taskbar it moves and hides with it (auto-hide, full-screen apps) and dies with it. A hidden
/// top-level window on the same thread hears "TaskbarCreated" (Explorer restarted), display and theme changes.
/// </para>
/// </summary>
internal sealed class TaskbarWidgetWindow : IDisposable
{
    private const string HostClassName = "HushMusic.TaskbarPlayerHost";
    private const string WidgetClassName = "HushMusic.TaskbarPlayer";
    private const uint StateMessage = Win32.WmApp + 20;
    private const uint ProbeMessage = Win32.WmApp + 21;
    private const uint StopMessage = Win32.WmApp + 22;
    private const int OpenAppCommand = 1;
    private const int HidePlayerCommand = 2;
    private const uint LayoutIntervalMs = 2500;
    private const uint VolumeOverlayMs = 1500;
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static readonly UIntPtr LayoutTimer = new(1);
    private static readonly UIntPtr OverlayTimer = new(2);

    // One window class per process for all instances, so the procedure never points at a collected delegate.
    private static readonly Win32.WndProc StaticProc = StaticWindowProc;
    private static readonly Lock ClassGate = new();
    private static bool s_registered;

    [ThreadStatic]
    private static TaskbarWidgetWindow? t_current;

    private readonly string? _display;
    private readonly ITaskbarWidgetSink _sink;
    private readonly ILogger _logger;
    private readonly uint _taskbarCreatedMessage = Win32.RegisterWindowMessage("TaskbarCreated");
    private readonly Thread _thread;
    private volatile TaskbarWidgetState _state;
    private volatile bool _stopping;
    private IntPtr _host;

    // Widget thread only from here on.
    private TaskbarWidgetRenderer? _renderer;
    private TaskbarProbe? _probe;
    private IntPtr _taskbar;
    private IntPtr _widget;
    private PixelRect? _placed;
    private TaskbarWidgetGeometry? _geometry;
    private int _reportedCoverSize;
    private bool _light;
    private TaskbarWidgetPart _hover;
    private TaskbarWidgetPart _pressed;
    private bool _trackingLeave;
    private int _wheelPending;
    private double? _overlayVolume;
    private bool _overlayMuted;
    private IntPtr _memoryDc;
    private IntPtr _dib;
    private IntPtr _bits;
    private int _dibWidth;
    private int _dibHeight;
    private string? _lastProblem;
    private bool _hadTrack;

    private TaskbarWidgetWindow(string? display, TaskbarWidgetState state, ITaskbarWidgetSink sink, ILogger logger)
    {
        _display = display;
        _state = state;
        _sink = sink;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Taskbar player" };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    /// <summary>"main taskbar" or "taskbar on Display 2", for the log.</summary>
    private string TaskbarName => _display is null ? "main taskbar" : "taskbar on " + TaskbarDisplayChoice.ShortName(_display);

    /// <summary>
    /// Starts the widget thread for the taskbar on <paramref name="display"/> (a device name; null for the main
    /// taskbar). The player appears once there is a track, the taskbar exists and it has room.
    /// </summary>
    public static TaskbarWidgetWindow Start(string? display, TaskbarWidgetState state, ITaskbarWidgetSink sink, ILogger logger)
    {
        var window = new TaskbarWidgetWindow(display, state, sink, logger);
        window._thread.Start();
        return window;
    }

    /// <summary>Shows a new snapshot. Any thread; cheap (stores it and posts a message).</summary>
    public void Update(TaskbarWidgetState state)
    {
        _state = state;
        Post(StateMessage);
    }

    /// <summary>Removes the player from the taskbar and ends the thread. Any thread.</summary>
    public void Dispose() => Dispose(TimeSpan.Zero);

    /// <summary>As <see cref="Dispose()"/>, waiting up to <paramref name="wait"/> for the window to be gone (app exit).</summary>
    public void Dispose(TimeSpan wait)
    {
        _stopping = true;
        Post(StopMessage);
        if (wait > TimeSpan.Zero && Thread.CurrentThread != _thread)
        {
            _thread.Join(wait);
        }
    }

    private void Post(uint message)
    {
        var host = Volatile.Read(ref _host);
        if (host != IntPtr.Zero)
        {
            Win32.PostMessage(host, message, IntPtr.Zero, IntPtr.Zero);
        }
    }

    private static IntPtr StaticWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        var current = t_current;
        if (current is null)
        {
            return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        try
        {
            if (hWnd == current._widget && current.WidgetMessage(msg, wParam, lParam) is { } widgetResult)
            {
                return widgetResult;
            }

            if (hWnd == current._host && current.HostMessage(msg, wParam, lParam) is { } hostResult)
            {
                return hostResult;
            }
        }
        catch (Exception ex)
        {
            // An exception must never unwind into the native window procedure (or into Explorer's input processing).
            current._logger.LogError(ex, "Taskbar player message {Message:X} failed", msg);
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Run()
    {
        t_current = this;
        var gdiPlus = false;
        try
        {
            gdiPlus = GdiPlus.Acquire();
            if (!gdiPlus || !RegisterClasses())
            {
                _logger.LogWarning("Taskbar player unavailable: GDI+ or the window class could not be set up");
                return;
            }

            var instance = Win32.GetModuleHandle(null);
            var host = Win32.CreateWindowEx(WidgetNative.WsExToolWindow, HostClassName, "Hush taskbar player host", WidgetNative.WsPopup, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (host == IntPtr.Zero)
            {
                _logger.LogWarning("Taskbar player unavailable: no host window (error {Error})", Marshal.GetLastPInvokeError());
                return;
            }

            Volatile.Write(ref _host, host);
            if (_stopping)
            {
                Win32.DestroyWindow(host);
                return;
            }

            _renderer = new TaskbarWidgetRenderer();
            _probe = new TaskbarProbe(host, ProbeMessage, _logger);
            _light = ReadLightTaskbar();
            WidgetNative.SetTimer(host, LayoutTimer, LayoutIntervalMs, IntPtr.Zero);
            Embed();

            while (WidgetNative.GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                WidgetNative.TranslateMessage(ref message);
                WidgetNative.DispatchMessage(ref message);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Taskbar player thread failed");
        }
        finally
        {
            DestroyWidget();
            if (_host != IntPtr.Zero && WidgetNative.IsWindow(_host))
            {
                Win32.DestroyWindow(_host);
            }

            Volatile.Write(ref _host, IntPtr.Zero);
            _probe?.Dispose();
            _renderer?.Dispose();
            FreeDib();
            if (gdiPlus)
            {
                GdiPlus.Release();
            }

            t_current = null;
            _logger.LogDebug("Taskbar player on the {Name} stopped", TaskbarName);
        }
    }

    private static bool RegisterClasses()
    {
        lock (ClassGate)
        {
            if (s_registered)
            {
                return true;
            }

            var instance = Win32.GetModuleHandle(null);
            var cursor = WidgetNative.LoadCursor(IntPtr.Zero, WidgetNative.IdcArrow);
            foreach (var name in new[] { HostClassName, WidgetClassName })
            {
                var windowClass = new Win32.WndClassEx
                {
                    Size = (uint)Marshal.SizeOf<Win32.WndClassEx>(),
                    WndProc = StaticProc,
                    Instance = instance,
                    Cursor = cursor,
                    ClassName = name,
                };

                if (Win32.RegisterClassEx(ref windowClass) == 0 && Marshal.GetLastPInvokeError() != 1410 /* ERROR_CLASS_ALREADY_EXISTS */)
                {
                    return false;
                }
            }

            s_registered = true;
            return true;
        }
    }

    private IntPtr? HostMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case StateMessage:
                OnStateChanged();
                return IntPtr.Zero;
            case ProbeMessage:
                Relayout();
                return IntPtr.Zero;
            case StopMessage:
                DestroyWidget();
                Win32.DestroyWindow(_host);
                return IntPtr.Zero;
            case Win32.WmDestroy:
                WidgetNative.PostQuitMessage(0);
                return IntPtr.Zero;
            case WidgetNative.WmTimer when (nuint)wParam == LayoutTimer:
                OnLayoutTimer();
                return IntPtr.Zero;
            case WidgetNative.WmTimer when (nuint)wParam == OverlayTimer:
                WidgetNative.KillTimer(_host, OverlayTimer);
                _overlayVolume = null;
                Render();
                return IntPtr.Zero;
            case WidgetNative.WmSettingChange:
                if (lParam != IntPtr.Zero && Marshal.PtrToStringUni(lParam) == "ImmersiveColorSet")
                {
                    _light = ReadLightTaskbar();
                    Render();
                }

                // Taskbar alignment and similar settings arrive here too.
                RequestProbe();
                return IntPtr.Zero;
            case WidgetNative.WmDisplayChange:
                // Displays added or removed may change which taskbars get a player.
                _sink.OnDisplaysChanged();
                RequestProbe();
                Relayout();
                return IntPtr.Zero;
        }

        if (msg == _taskbarCreatedMessage && msg != 0)
        {
            _logger.LogInformation("Explorer restarted; putting the taskbar player back on the {Name}", TaskbarName);
            Embed();
            return IntPtr.Zero;
        }

        return null;
    }

    private IntPtr? WidgetMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // Clicking the player must not activate the taskbar (it would close our flyout before the click lands).
            case WidgetNative.WmMouseActivate:
                return WidgetNative.MaNoActivate;
            case WidgetNative.WmSetCursor:
                WidgetNative.SetCursor(WidgetNative.LoadCursor(IntPtr.Zero, WidgetNative.IdcArrow));
                return 1;
            case WidgetNative.WmMouseMove:
                OnMouseMove(Win32.SignedLowWord(lParam), Win32.SignedHighWord(lParam));
                return IntPtr.Zero;
            case WidgetNative.WmMouseLeave:
                _trackingLeave = false;
                SetHover(TaskbarWidgetPart.None);
                return IntPtr.Zero;
            case WidgetNative.WmLButtonDown:
                _pressed = Hit(Win32.SignedLowWord(lParam), Win32.SignedHighWord(lParam));
                WidgetNative.SetCapture(_widget);
                Render();
                return IntPtr.Zero;
            case WidgetNative.WmLButtonUp:
                OnLeftButtonUp(Win32.SignedLowWord(lParam), Win32.SignedHighWord(lParam));
                return IntPtr.Zero;
            case WidgetNative.WmCaptureChanged:
                if (_pressed != TaskbarWidgetPart.None)
                {
                    _pressed = TaskbarWidgetPart.None;
                    Render();
                }

                return IntPtr.Zero;

            // Handled here so nothing reaches the taskbar (its own menu, or its wheel handling).
            case WidgetNative.WmRButtonDown or WidgetNative.WmMButtonDown or WidgetNative.WmMouseHWheel or Win32.WmContextMenu:
                return IntPtr.Zero;
            case WidgetNative.WmRButtonUp:
                ShowMenu();
                return IntPtr.Zero;
            case WidgetNative.WmMouseWheel:
                OnWheel(Win32.SignedHighWord(wParam));
                return IntPtr.Zero;
            case WidgetNative.WmDpiChangedAfterParent:
                Relayout();
                return IntPtr.Zero;
            case Win32.WmDestroy:
                // Destroyed with its parent (Explorer exiting) or by DestroyWidget.
                _widget = IntPtr.Zero;
                _placed = null;
                return IntPtr.Zero;
        }

        return null;
    }

    private void OnStateChanged()
    {
        var state = _state;
        if (!state.HasTrack)
        {
            _overlayVolume = null;
        }
        else if (!_hadTrack)
        {
            // The layout isn't read while there is nothing to show (see OnLayoutTimer): read it fresh.
            RequestProbe();
        }

        _hadTrack = state.HasTrack;
        if ((_placed is not null) != state.HasTrack)
        {
            Relayout();
        }
        else
        {
            Render();
        }
    }

    private void OnLayoutTimer()
    {
        var taskbar = TaskbarWindows.Find(_display);
        if (taskbar != _taskbar || _widget == IntPtr.Zero || !WidgetNative.IsWindow(_widget) || WidgetNative.GetParent(_widget) != taskbar)
        {
            Embed();
            return;
        }

        // With nothing playing the player stays hidden: keep checking that the taskbar is there (cheap), but leave its
        // layout, a UI Automation read of Explorer, until there is a track again.
        if (_state.HasTrack)
        {
            RequestProbe();
        }

        Relayout();
    }

    private void RequestProbe()
    {
        if (_taskbar != IntPtr.Zero)
        {
            _probe?.Request(_taskbar);
        }
    }

    // Creates the player window and parents it into the current taskbar (again after Explorer restarts).
    private void Embed()
    {
        DestroyWidget();
        _taskbar = TaskbarWindows.Find(_display);
        if (_taskbar == IntPtr.Zero)
        {
            // Another display's taskbar is missing whenever "Show my taskbar on all displays" is off: not worth a warning.
            Problem($"No {TaskbarName} yet; the taskbar player waits for it", _display is null ? LogLevel.Warning : LogLevel.Debug);
            return;
        }

        var instance = Win32.GetModuleHandle(null);
        var widget = Win32.CreateWindowEx(
            WidgetNative.WsExLayered | WidgetNative.WsExToolWindow | WidgetNative.WsExNoActivate | WidgetNative.WsExNoParentNotify,
            WidgetClassName,
            "Hush taskbar player",
            WidgetNative.WsPopup,
            0,
            0,
            1,
            1,
            IntPtr.Zero,
            IntPtr.Zero,
            instance,
            IntPtr.Zero);
        if (widget == IntPtr.Zero)
        {
            Problem($"Could not create the taskbar player window (error {Marshal.GetLastPInvokeError()})");
            return;
        }

        _widget = widget;

        // A popup becomes a child: swap the style first, then reparent (SetParent's documented order).
        WidgetNative.SetWindowLongPtr(widget, WidgetNative.GwlStyle, (IntPtr)(nint)(WidgetNative.WsChild | WidgetNative.WsClipSiblings));
        if (WidgetNative.SetParent(widget, _taskbar) == IntPtr.Zero && WidgetNative.GetParent(widget) != _taskbar)
        {
            Problem($"Could not attach the player to the taskbar (error {Marshal.GetLastPInvokeError()})");
            DestroyWidget();
            return;
        }

        _lastProblem = null;
        _logger.LogDebug("Taskbar player attached to the {Name} ({Taskbar:X})", TaskbarName, (long)_taskbar);
        RequestProbe();
        Relayout();
    }

    private void DestroyWidget()
    {
        var widget = _widget;
        _widget = IntPtr.Zero;
        _placed = null;
        _hover = _pressed = TaskbarWidgetPart.None;
        _trackingLeave = false;
        if (widget != IntPtr.Zero && WidgetNative.IsWindow(widget))
        {
            Win32.DestroyWindow(widget);
        }
    }

    // Where the player goes now; hides it when there is no track, no room or no taskbar to measure.
    private void Relayout()
    {
        if (_widget == IntPtr.Zero)
        {
            return;
        }

        var scale = 1.0;
        var place = _state.HasTrack ? Place(out scale) : null;
        if (place is not { } rect)
        {
            if (_placed is not null || WidgetNative.IsWindowVisible(_widget))
            {
                WidgetNative.SetWindowPos(_widget, IntPtr.Zero, 0, 0, 0, 0, WidgetNative.SwpHideWindow | WidgetNative.SwpNoMove | WidgetNative.SwpNoSize | WidgetNative.SwpNoActivate);
                _logger.LogDebug("Taskbar player hidden ({Reason})", _state.HasTrack ? "no room on the taskbar" : "nothing playing");
            }

            _placed = null;
            return;
        }

        if (_geometry is null || _geometry.Width != rect.Width || _geometry.Height != rect.Height || Math.Abs(_geometry.Scale - scale) > 0.001)
        {
            _geometry = TaskbarWidgetLayout.Measure(rect.Width, rect.Height, scale);
            if (_geometry.Cover.Width != _reportedCoverSize)
            {
                _reportedCoverSize = _geometry.Cover.Width;
                _sink.OnCoverSizeChanged(_reportedCoverSize);
            }
        }

        // Keep it above the taskbar's own content (Explorer may reorder its children) and in place.
        var onTop = WidgetNative.GetWindow(_widget, WidgetNative.GwHwndPrev) == IntPtr.Zero;
        if (_placed != rect || !onTop || !WidgetNative.IsWindowVisible(_widget))
        {
            Render(rect.Width, rect.Height);
            WidgetNative.SetWindowPos(_widget, WidgetNative.HwndTop, rect.X, rect.Y, rect.Width, rect.Height, WidgetNative.SwpNoActivate | WidgetNative.SwpShowWindow);
            if (_placed != rect)
            {
                _logger.LogDebug("Taskbar player on the {Name} at {X},{Y} {Width}x{Height} (scale {Scale})", TaskbarName, rect.X, rect.Y, rect.Width, rect.Height, scale);
            }

            _placed = rect;
        }
    }

    // The taskbar as the layout sees it, in taskbar-client pixels, from UI Automation or the legacy HWNDs.
    private PixelRect? Place(out double scale)
    {
        scale = 1;
        if (!WidgetNative.GetClientRect(_taskbar, out var client))
        {
            return null;
        }

        var origin = default(Win32.Point);
        WidgetNative.ClientToScreen(_taskbar, ref origin);
        var dpi = Win32.GetDpiForWindow(_taskbar);
        scale = (dpi == 0 ? 96 : dpi) / 96.0;

        PixelSpan Span(PixelRect screen) => new(screen.X - origin.X, screen.Right - origin.X);

        // Wait for the first UI Automation answer (tens of ms) rather than flash up where a mod already draws.
        if (_probe?.Latest is not { } probe || probe.Taskbar != _taskbar)
        {
            return null;
        }

        PixelSpan? icons = null;
        IReadOnlyList<PixelSpan> occupied = [];
        if (probe.Elements is { } elements)
        {
            var spans = elements.Select(e => Span(e.Bounds)).ToList();
            var start = IndexOf(elements, "StartButton");
            var repeater = IndexOf(elements, "TaskbarFrameRepeater");
            if (start >= 0 || repeater >= 0)
            {
                (var group, occupied) = TaskbarWidgetLayout.SplitIcons(spans, spans[start >= 0 ? start : repeater], (int)Math.Round(8 * scale));
                icons = group;
            }
        }

        if (icons is null)
        {
            // Legacy windows Explorer still keeps in step with the XAML taskbar (hidden "Start", the task list).
            var startWindow = WidgetNative.FindWindowEx(_taskbar, IntPtr.Zero, "Start", null);
            if (startWindow != IntPtr.Zero && Win32.GetWindowRect(startWindow, out var startRect) && startRect.Right > startRect.Left)
            {
                var right = startRect.Right;
                var rebar = WidgetNative.FindWindowEx(_taskbar, IntPtr.Zero, "ReBarWindow32", null);
                var tasks = rebar == IntPtr.Zero ? IntPtr.Zero : WidgetNative.FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null);
                if (tasks != IntPtr.Zero && Win32.GetWindowRect(tasks, out var tasksRect))
                {
                    right = Math.Max(right, tasksRect.Right);
                }

                icons = new PixelSpan(startRect.Left - origin.X, right - origin.X);
            }
        }

        // The taskbars on other displays have no TrayNotifyWnd; their clock shows up in UI Automation.
        var trayLeft = client.Right;
        var tray = WidgetNative.FindWindowEx(_taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray != IntPtr.Zero && Win32.GetWindowRect(tray, out var trayRect) && trayRect.Right > trayRect.Left)
        {
            trayLeft = Math.Clamp(trayRect.Left - origin.X, 0, client.Right);
        }
        else if (probe.TrayLeft is { } probedTray)
        {
            trayLeft = Math.Clamp(probedTray - origin.X, 0, client.Right);
        }

        var place = TaskbarWidgetLayout.Place(new TaskbarContent(client.Right, client.Bottom, scale, icons, trayLeft, occupied));
        if (place is null)
        {
            if (icons is null)
            {
                Problem("Can't tell where the taskbar icons are; the taskbar player stays hidden");
            }
            else
            {
                Problem("No room for the taskbar player", LogLevel.Debug);
            }
        }

        return place;
    }

    private static int IndexOf(IReadOnlyList<TaskbarElement> elements, string automationId)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            if (elements[i].AutomationId == automationId)
            {
                return i;
            }
        }

        return -1;
    }

    private void Render() => Render(_placed?.Width ?? 0, _placed?.Height ?? 0);

    private void Render(int width, int height)
    {
        if (_widget == IntPtr.Zero || _renderer is null || _geometry is not { } geometry || width <= 0 || height <= 0)
        {
            return;
        }

        if (!EnsureDib(width, height))
        {
            return;
        }

        var visuals = new WidgetVisuals(_hover, _pressed, _overlayVolume, _overlayMuted, _light);
        _renderer.Render(_bits, width * 4, geometry, _state, visuals);

        var size = new WidgetNative.Size(width, height);
        var source = default(Win32.Point);
        var blend = new WidgetNative.BlendFunction { BlendOp = WidgetNative.AcSrcOver, SourceConstantAlpha = 255, AlphaFormat = WidgetNative.AcSrcAlpha };
        if (!WidgetNative.UpdateLayeredWindow(_widget, IntPtr.Zero, IntPtr.Zero, ref size, _memoryDc, ref source, 0, ref blend, WidgetNative.UlwAlpha))
        {
            Problem($"UpdateLayeredWindow failed (error {Marshal.GetLastPInvokeError()})");
        }
    }

    private bool EnsureDib(int width, int height)
    {
        if (_dib != IntPtr.Zero && _dibWidth == width && _dibHeight == height)
        {
            return true;
        }

        FreeDib();
        _memoryDc = WidgetNative.CreateCompatibleDC(IntPtr.Zero);
        var header = new WidgetNative.BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<WidgetNative.BitmapInfoHeader>(),
            Width = width,
            Height = -height, // top-down
            Planes = 1,
            BitCount = 32,
        };
        _dib = WidgetNative.CreateDIBSection(_memoryDc, ref header, 0 /* DIB_RGB_COLORS */, out _bits, IntPtr.Zero, 0);
        if (_dib == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            Problem("Could not create the taskbar player's bitmap");
            FreeDib();
            return false;
        }

        WidgetNative.SelectObject(_memoryDc, _dib);
        _dibWidth = width;
        _dibHeight = height;
        return true;
    }

    private void FreeDib()
    {
        if (_memoryDc != IntPtr.Zero)
        {
            WidgetNative.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            WidgetNative.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        _bits = IntPtr.Zero;
        _dibWidth = _dibHeight = 0;
    }

    private TaskbarWidgetPart Hit(int x, int y) =>
        _geometry is { } geometry ? TaskbarWidgetLayout.HitTest(geometry, x, y) : TaskbarWidgetPart.None;

    private void OnMouseMove(int x, int y)
    {
        if (!_trackingLeave)
        {
            var track = new WidgetNative.TrackMouseEventData
            {
                Size = (uint)Marshal.SizeOf<WidgetNative.TrackMouseEventData>(),
                Flags = WidgetNative.TmeLeave,
                Window = _widget,
            };
            _trackingLeave = WidgetNative.TrackMouseEvent(ref track);
        }

        SetHover(Hit(x, y));
    }

    private void SetHover(TaskbarWidgetPart part)
    {
        if (_hover != part)
        {
            _hover = part;
            Render();
        }
    }

    private void OnLeftButtonUp(int x, int y)
    {
        var pressed = _pressed;
        _pressed = TaskbarWidgetPart.None;
        if (WidgetNative.GetCapture() == _widget)
        {
            WidgetNative.ReleaseCapture();
        }

        var part = Hit(x, y);
        Render();
        if (pressed == TaskbarWidgetPart.None || part != pressed)
        {
            return;
        }

        switch (part)
        {
            case TaskbarWidgetPart.Previous:
                _sink.OnCommand(TaskbarWidgetCommand.Previous);
                break;
            case TaskbarWidgetPart.PlayPause:
                _sink.OnCommand(TaskbarWidgetCommand.PlayPause);
                break;
            case TaskbarWidgetPart.Next:
                _sink.OnCommand(TaskbarWidgetCommand.Next);
                break;
            case TaskbarWidgetPart.Content when Anchor() is { } anchor:
                _sink.OnToggleFlyout(anchor);
                break;
        }
    }

    private TaskbarWidgetAnchor? Anchor()
    {
        if (!Win32.GetWindowRect(_widget, out var widget) || !Win32.GetWindowRect(_taskbar, out var taskbar))
        {
            return null;
        }

        static PixelRect ToPixels(Win32.Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        return new TaskbarWidgetAnchor(ToPixels(widget), ToPixels(taskbar), Win32.GetDpiForWindow(_taskbar));
    }

    private void OnWheel(int delta)
    {
        var state = _state;
        var notches = TaskbarWidgetLayout.TakeNotches(ref _wheelPending, delta);
        if (notches == 0 || !state.HasTrack)
        {
            return;
        }

        // Shown at once; the app's own volume (player bar, saved setting) catches up through the next snapshot.
        var showing = _overlayVolume is not null;
        _overlayVolume = TaskbarWidgetLayout.StepVolume(_overlayVolume ?? state.Volume, notches);
        _overlayMuted = (showing ? _overlayMuted : state.IsMuted) && notches < 0;
        _sink.OnVolumeNotches(notches);
        WidgetNative.SetTimer(_host, OverlayTimer, VolumeOverlayMs, IntPtr.Zero);
        Render();
    }

    private void ShowMenu()
    {
        if (!Win32.GetCursorPos(out var point))
        {
            return;
        }

        TrayIcon.ApplyMenuTheme(dark: !_light);
        var menu = Win32.CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        int command;
        try
        {
            Win32.AppendMenu(menu, WidgetNative.MfString, (UIntPtr)OpenAppCommand, "Open Hush");
            Win32.AppendMenu(menu, WidgetNative.MfSeparator, UIntPtr.Zero, null);
            Win32.AppendMenu(menu, WidgetNative.MfString, (UIntPtr)HidePlayerCommand, "Hide taskbar player");

            // The menu only closes on an outside click if its owner is the foreground window (KB135788).
            Win32.SetForegroundWindow(_host);
            command = WidgetNative.TrackPopupMenuReturnCommand(
                menu,
                WidgetNative.TpmReturnCmd | WidgetNative.TpmRightButton | WidgetNative.TpmBottomAlign | WidgetNative.TpmNoNotify,
                point.X,
                point.Y,
                _host,
                IntPtr.Zero);
            Win32.PostMessage(_host, Win32.WmNull, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            Win32.DestroyMenu(menu);
        }

        _hover = TaskbarWidgetPart.None;
        Render();
        if (command == OpenAppCommand)
        {
            _sink.OnCommand(TaskbarWidgetCommand.OpenApp);
        }
        else if (command == HidePlayerCommand)
        {
            _sink.OnCommand(TaskbarWidgetCommand.HidePlayer);
        }
    }

    // The taskbar follows Windows' "system" mode, which can differ from the app mode (and from this app's theme).
    private bool ReadLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read the taskbar theme");
            return false;
        }
    }

    // Logged once per distinct problem, so a 2.5 s retry doesn't flood the log.
    private void Problem(string message, LogLevel level = LogLevel.Warning)
    {
        if (message != _lastProblem)
        {
            _lastProblem = message;
            _logger.Log(level, "{Problem}", message);
        }
    }
}
