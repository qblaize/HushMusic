using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using HushMusic.App.Hosting;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Windowing;
using HushMusic.App.Services.Windowing.TaskbarWidget;
using HushMusic.Core.Services;

namespace HushMusic.App.Controls.TaskbarFlyout;

/// <summary>
/// The window that pops up above the taskbar player: no title bar or frame, rounded corners, desktop acrylic, always
/// on top, not in Alt+Tab or the taskbar, as tall as its content. Created once and shown/hidden; closes when it loses
/// activation, on Esc, on a click outside it, or when the player is clicked again. UI thread.
/// </summary>
internal sealed class TaskbarFlyoutWindow : Window
{
    private const double FlyoutWidth = 360;
    private const double FallbackHeight = 242;
    private const double Gap = 12;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int DwmwaExtendedFrameBounds = 9;
    private static readonly TimeSpan ReopenGuard = TimeSpan.FromMilliseconds(350);

    private readonly TaskbarFlyoutView _view;
    private readonly IThemeService _theme;
    private readonly bool _testInstance;
    private readonly IntPtr _hwnd;
    private readonly DispatcherQueueTimer _outsideClick;
    private readonly ILogger _logger;
    private readonly List<DispatcherQueueTimer> _testTimers = [];
    private TaskbarWidgetAnchor _anchor;
    private WindowFrame _frame;
    private uint _frameDpi;
    private int _framePasses;
    private DateTime _hiddenAt;
    private bool _closing;

    public TaskbarFlyoutWindow(IThemeService theme, bool testInstance)
    {
        _theme = theme;
        _testInstance = testInstance;
        _logger = App.GetService<ILoggerFactory>().CreateLogger<TaskbarFlyoutWindow>();
        _view = new TaskbarFlyoutView();
        _view.CloseRequested += (_, _) => Hide();

        // The window is as tall as its content: refit when the "Up next" row comes or goes, and once the content has
        // loaded (the first opening can only guess the height).
        _view.PreferredHeightChanged += (_, _) => DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Refit);
        _view.Loaded += (_, _) => Refit();
        Content = _view;
        Title = "Hush player";
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;

        // Test instances stay behind the user's windows.
        presenter.IsAlwaysOnTop = !testInstance;
        // A fully borderless presenter leaves a 3 px light frame strip around the content. Keep the thin border (it gives the
        // system shadow and rounded corners), extend the content over it, and hide its colour below.
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        AppWindow.SetPresenter(presenter);
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Collapsed;
        AppWindow.IsShownInSwitchers = false;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var round = 2; // DWMWCP_ROUND
        _ = DwmSetWindowAttribute(_hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref round, sizeof(int));

        // Windows 11 draws a 1 px window border (light grey, or the accent while active) on top of the flyout's own
        // hairline; drop it so only the soft in-app edge remains.
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        _ = DwmSetWindowAttribute(_hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));

        _outsideClick = DispatcherQueue.CreateTimer();
        _outsideClick.Interval = TimeSpan.FromMilliseconds(150);
        _outsideClick.Tick += (_, _) => CheckOutsideClick();

        ApplyTheme();
        _theme.ThemeChanged += OnThemeChanged;
        Activated += OnActivated;
        AppWindow.Closing += OnClosing;
    }

    public bool IsOpen { get; private set; }

    /// <summary>The player was clicked: close if open, else open above it.</summary>
    public void Toggle(TaskbarWidgetAnchor anchor, bool activate)
    {
        if (IsOpen)
        {
            Hide();
            return;
        }

        // The same click already closed it (the flyout lost activation as the mouse went down on the taskbar).
        if (DateTime.UtcNow - _hiddenAt < ReopenGuard)
        {
            return;
        }

        ShowAt(anchor, activate);
    }

    /// <summary>Opens above <paramref name="anchor"/>; <paramref name="activate"/> false shows it without focus (tests).</summary>
    public void ShowAt(TaskbarWidgetAnchor anchor, bool activate)
    {
        _anchor = anchor;
        _framePasses = 0;
        Place(moveFirst: true);

        IsOpen = true;
        if (activate)
        {
            AppWindow.Show(activateWindow: true);
            Activate();
            _view.Focus(FocusState.Programmatic);
            _outsideClick.Start();
        }
        else
        {
            AppWindow.Show(activateWindow: false);
            TestWindowPlacement.SendToBack(this);
        }

        _view.PlayOpenAnimation();
    }

    public void Hide()
    {
        _outsideClick.Stop();
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _hiddenAt = DateTime.UtcNow;
        AppWindow.Hide();
    }

    /// <summary>Really closes the window (app exit): a hidden window still counts as open and keeps the app running.</summary>
    public void CloseForGood()
    {
        _closing = true;
        _outsideClick.Stop();
        _testTimers.ForEach(t => t.Stop());
        _theme.ThemeChanged -= OnThemeChanged;
        _view.Dispose();
        Close();
    }

    /// <summary>Test hook: after <paramref name="delay"/>, clicks "Up next" tile <paramref name="position"/> (1-based).</summary>
    public void PlayUpNextForTest(int position, TimeSpan delay) => RunForTest(delay, () =>
        _logger.LogInformation("Test: clicked Up next tile {Position} ({Tile})", position, _view.InvokeUpNextTileForTest(position) ?? "none"));

    /// <summary>Test hook: after <paramref name="delay"/>, shows "Up next" tile <paramref name="position"/> as hovered.</summary>
    public void HoverUpNextForTest(int position, TimeSpan delay) => RunForTest(delay, () =>
        _logger.LogInformation("Test: hovered Up next tile {Position}: {Done}", position, _view.HoverUpNextTileForTest(position)));

    // Kept in a field: an unreferenced DispatcherQueueTimer can be collected before it fires.
    private void RunForTest(TimeSpan delay, Action action)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => action();
        _testTimers.Add(timer);
        timer.Start();
    }

    // Sizes the window so its content is FlyoutWidth wide and as tall as it wants, and puts it next to the taskbar: the
    // visible edge facing the taskbar stays Gap away from it (the window grows away from the taskbar), kept on the display.
    private void Place(bool moveFirst)
    {
        var anchor = _anchor;
        var dpi = anchor.Dpi == 0 ? 96u : anchor.Dpi;
        var scale = dpi / 96.0;
        var frame = _frameDpi == dpi ? _frame : default;
        var width = (int)Math.Round(FlyoutWidth * scale) + frame.Width - frame.Left - frame.Right;
        var height = (int)Math.Round(ContentHeight() * scale) + frame.Height - frame.Top - frame.Bottom;
        var gap = (int)Math.Round(Gap * scale);

        var display = DisplayArea.GetFromRect(new RectInt32(anchor.Widget.X, anchor.Widget.Y, Math.Max(1, anchor.Widget.Width), Math.Max(1, anchor.Widget.Height)), DisplayAreaFallback.Primary).OuterBounds;
        var taskbarAtTop = anchor.Taskbar.Y + (anchor.Taskbar.Height / 2) < display.Y + (display.Height / 2);
        var x = Math.Clamp(anchor.Widget.X, display.X + gap, Math.Max(display.X + gap, display.X + display.Width - width - gap));
        var y = taskbarAtTop ? anchor.Taskbar.Bottom + gap : anchor.Taskbar.Y - gap - height;
        y = Math.Clamp(y, display.Y + gap, Math.Max(display.Y + gap, display.Y + display.Height - height - gap));

        // (x, y, width, height) is what shows; the window rect also holds the invisible frame.
        var rect = new RectInt32(x - frame.Left, y - frame.Top, width + frame.Left + frame.Right, height + frame.Top + frame.Bottom);
        var moved = moveFirst || AppWindow.Position.X != rect.X || AppWindow.Position.Y != rect.Y
            || AppWindow.Size.Width != rect.Width || AppWindow.Size.Height != rect.Height;
        if (moved)
        {
            // Move first, then size: landing on a display with another DPI would otherwise rescale the size we set.
            if (moveFirst)
            {
                AppWindow.Move(new PointInt32(rect.X, rect.Y));
            }

            AppWindow.MoveAndResize(rect);
            _logger.LogDebug("Taskbar flyout shows at {X},{Y} {Width}x{Height} (taskbar {Top}..{Bottom})", x, y, width, height, anchor.Taskbar.Y, anchor.Taskbar.Bottom);
        }

        if (moved || _frameDpi != dpi)
        {
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, CheckFrame);
        }
    }

    // Once a placement has been laid out, measures the frame around the content and places the window again if Place
    // assumed a different one (the first opening, a new DPI). The presenter keeps an invisible resize border on the
    // left, right and bottom plus a 1 px border, so the window rect is bigger than both what shows and the content.
    private void CheckFrame()
    {
        if (!IsOpen || _view.XamlRoot is not { } root || root.Size.Width <= 0 || !Win32.GetWindowRect(_hwnd, out var window)
            || DwmGetWindowAttribute(_hwnd, DwmwaExtendedFrameBounds, out var visible, Marshal.SizeOf<Win32.Rect>()) != 0)
        {
            return;
        }

        var scale = root.RasterizationScale;
        var frame = new WindowFrame(
            visible.Left - window.Left,
            visible.Top - window.Top,
            window.Right - visible.Right,
            window.Bottom - visible.Bottom,
            window.Right - window.Left - (int)Math.Round(root.Size.Width * scale),
            window.Bottom - window.Top - (int)Math.Round(root.Size.Height * scale));
        var dpi = (uint)Math.Round(scale * 96);
        if (frame == _frame && dpi == _frameDpi)
        {
            return;
        }

        _logger.LogDebug("Taskbar flyout frame {Frame} at {Dpi} dpi", frame, dpi);
        _frame = frame;
        _frameDpi = dpi;
        if (++_framePasses <= 3)
        {
            Place(moveFirst: false);
        }
    }

    // The content changed height while the flyout is open (or loaded after the first opening used the fallback height).
    private void Refit()
    {
        if (IsOpen)
        {
            Place(moveFirst: false);
        }
    }

    // Measured once the content has a XamlRoot; the first opening uses the designed height until the content loads.
    private double ContentHeight()
    {
        if (_view.XamlRoot is null)
        {
            return FallbackHeight;
        }

        _view.Measure(new Windows.Foundation.Size(FlyoutWidth, double.PositiveInfinity));
        var height = _view.DesiredSize.Height;
        return height > 100 ? Math.Ceiling(height) : FallbackHeight;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsOpen && !_testInstance)
        {
            Hide();
        }
    }

    // Normally losing activation closes it; this covers the case where Windows refused to activate it.
    private void CheckOutsideClick()
    {
        if (!IsOpen || GetForegroundWindow() == _hwnd)
        {
            return;
        }

        if ((GetAsyncKeyState(VkLButton) & 0x8000) == 0 && (GetAsyncKeyState(VkRButton) & 0x8000) == 0)
        {
            return;
        }

        if (!Win32.GetCursorPos(out var cursor) || Contains(AppWindow.Position, AppWindow.Size, cursor) || _anchor.Widget.Contains(cursor.X, cursor.Y))
        {
            return;
        }

        Hide();
    }

    private static bool Contains(PointInt32 position, SizeInt32 size, Win32.Point point) =>
        new PixelRect(position.X, position.Y, size.Width, size.Height).Contains(point.X, point.Y);

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_closing)
        {
            // Alt+F4 on the flyout just hides it.
            args.Cancel = true;
            Hide();
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => ApplyTheme();

    private void ApplyTheme() => _view.RequestedTheme = _theme.ActualTheme;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Win32.Rect value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>
    /// Physical pixels: <see cref="Left"/> … <see cref="Bottom"/> lie between the window rect and what shows of it (DWM's
    /// extended frame bounds); <see cref="Width"/> and <see cref="Height"/> are how much bigger the window is than its content.
    /// </summary>
    private readonly record struct WindowFrame(int Left, int Top, int Right, int Bottom, int Width, int Height);
}
