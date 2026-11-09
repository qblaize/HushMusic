using System.Runtime.InteropServices;

namespace HushMusic.App.Services.Windowing;

/// <summary>
/// Whether other windows cover all of a shown window, so none of it can be seen although it is neither minimized nor
/// hidden (e.g. the app sits behind a maximized browser). Walks the windows above it in z-order and subtracts each one
/// that is surely opaque. Windows that may be see-through (layered with transparency or per-pixel alpha, click-through,
/// tool windows, cloaked) and the app's own windows don't count, so a doubtful case reads as visible.
/// </summary>
/// <remarks>Only reads window state (no messages are sent), so a hung window can't stall the caller.</remarks>
internal static class WindowCoverage
{
    private const uint GwHwndPrev = 3;
    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExToolWindow = 0x80;
    private const long WsExLayered = 0x80000;
    private const uint LwaColorKey = 0x1;
    private const uint LwaAlpha = 0x2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;
    private const int RgnAnd = 1;
    private const int RgnOr = 2;
    private const int RgnDiff = 4;
    private const int NullRegion = 1;
    private const int RegionError = 0;

    // A z-order walk normally ends after a few hundred windows; this only guards against a list changing mid-walk.
    private const int MaxWindowsAbove = 4000;

    /// <summary>True when nothing of <paramref name="hwnd"/> can be seen: other windows or the screen edges hide all of it.</summary>
    public static bool IsCovered(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        // Shown on another virtual desktop.
        if (IsCloaked(hwnd))
        {
            return true;
        }

        if (!TryGetBounds(hwnd, out var own))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var ownProcess);
        var visible = CreateRectRgn(own.Left, own.Top, own.Right, own.Bottom);
        var scratch = CreateRectRgn(0, 0, 0, 0);
        var screens = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (visible == IntPtr.Zero || scratch == IntPtr.Zero || screens == IntPtr.Zero || !AddScreens(screens, scratch))
            {
                return false;
            }

            // Only the part on a display can be seen.
            var result = CombineRgn(visible, visible, screens, RgnAnd);
            if (result == RegionError)
            {
                return false;
            }

            var above = hwnd;
            for (var i = 0; i < MaxWindowsAbove && result != NullRegion; i++)
            {
                above = GetWindow(above, GwHwndPrev);
                if (above == IntPtr.Zero)
                {
                    break;
                }

                if (!IsOpaqueCover(above, ownProcess, out var bounds))
                {
                    continue;
                }

                SetRectRgn(scratch, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                result = CombineRgn(visible, visible, scratch, RgnDiff);
                if (result == RegionError)
                {
                    return false;
                }
            }

            return result == NullRegion;
        }
        finally
        {
            DeleteObject(visible);
            DeleteObject(scratch);
            DeleteObject(screens);
        }
    }

    // A window counts only when it is surely drawn opaque over everything below it.
    private static bool IsOpaqueCover(IntPtr hwnd, uint ownProcess, out Win32.Rect bounds)
    {
        bounds = default;
        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var process);
        if (process == ownProcess)
        {
            return false;
        }

        var exStyle = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        if ((exStyle & (WsExTransparent | WsExToolWindow)) != 0)
        {
            return false;
        }

        // Layered windows are opaque only with a plain alpha of 255. UpdateLayeredWindow ones (per-pixel alpha) fail the
        // query and don't count.
        if ((exStyle & WsExLayered) != 0
            && (!GetLayeredWindowAttributes(hwnd, out _, out var alpha, out var flags)
                || (flags & LwaColorKey) != 0
                || ((flags & LwaAlpha) != 0 && alpha != 255)))
        {
            return false;
        }

        return !IsCloaked(hwnd) && TryGetBounds(hwnd, out bounds);
    }

    // The frame as drawn, without the invisible resize borders GetWindowRect includes.
    private static bool TryGetBounds(IntPtr hwnd, out Win32.Rect bounds)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out bounds, Marshal.SizeOf<Win32.Rect>()) != 0
            && !Win32.GetWindowRect(hwnd, out bounds))
        {
            return false;
        }

        return bounds.Right > bounds.Left && bounds.Bottom > bounds.Top;
    }

    private static bool IsCloaked(IntPtr hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

    // The union of all display rectangles (displays of different sizes leave gaps no window can be seen in).
    private static bool AddScreens(IntPtr screens, IntPtr scratch)
    {
        var ok = true;
        MonitorEnumProc add = (_, _, ref rect, _) =>
        {
            SetRectRgn(scratch, rect.Left, rect.Top, rect.Right, rect.Bottom);
            ok &= CombineRgn(screens, screens, scratch, RgnOr) != RegionError;
            return true;
        };
        var enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, add, IntPtr.Zero);
        GC.KeepAlive(add);
        return enumerated && ok;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref Win32.Rect rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hWnd, out uint colorKey, out byte alpha, out uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Win32.Rect value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetRectRgn(IntPtr region, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern int CombineRgn(IntPtr destination, IntPtr source1, IntPtr source2, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
