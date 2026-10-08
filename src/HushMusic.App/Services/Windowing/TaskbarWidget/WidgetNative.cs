using System.Runtime.InteropServices;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>Win32 declarations for the taskbar player: child windows, layered windows, DIBs, the message loop and input.</summary>
internal static class WidgetNative
{
    public const uint WsPopup = 0x80000000;
    public const uint WsChild = 0x40000000;
    public const uint WsClipSiblings = 0x04000000;
    public const uint WsExToolWindow = 0x00000080;
    public const uint WsExNoParentNotify = 0x00000004;
    public const uint WsExLayered = 0x00080000;
    public const uint WsExNoActivate = 0x08000000;
    public const int GwlStyle = -16;
    public const uint GwHwndPrev = 3;

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpHideWindow = 0x0080;
    public static readonly IntPtr HwndTop = IntPtr.Zero;

    public const uint WmTimer = 0x0113;
    public const uint WmSetCursor = 0x0020;
    public const uint WmMouseActivate = 0x0021;
    public const uint WmSettingChange = 0x001A;
    public const uint WmDisplayChange = 0x007E;
    public const uint WmMouseMove = 0x0200;
    public const uint WmLButtonDown = 0x0201;
    public const uint WmLButtonUp = 0x0202;
    public const uint WmRButtonDown = 0x0204;
    public const uint WmRButtonUp = 0x0205;
    public const uint WmMButtonDown = 0x0207;
    public const uint WmMouseWheel = 0x020A;
    public const uint WmMouseHWheel = 0x020E;
    public const uint WmCaptureChanged = 0x0215;
    public const uint WmMouseLeave = 0x02A3;
    public const uint WmDpiChangedAfterParent = 0x02E3;
    public const int MaNoActivate = 3;
    public const uint TmeLeave = 0x00000002;
    public const int IdcArrow = 32512;

    public const uint MfString = 0x0;
    public const uint MfSeparator = 0x800;
    public const uint TpmRightButton = 0x0002;
    public const uint TpmBottomAlign = 0x0020;
    public const uint TpmNoNotify = 0x0080;
    public const uint TpmReturnCmd = 0x0100;

    public const uint UlwAlpha = 0x2;
    public const byte AcSrcOver = 0x0;
    public const byte AcSrcAlpha = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public Win32.Point Point;
        public uint Private;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct Size(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BlendFunction
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TrackMouseEventData
    {
        public uint Size;
        public uint Flags;
        public IntPtr Window;
        public uint HoverTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    public static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetClientRect(IntPtr hWnd, out Win32.Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ClientToScreen(IntPtr hWnd, ref Win32.Point point);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr screenDc, IntPtr destination, ref Size size, IntPtr sourceDc, ref Win32.Point source, uint colorKey, ref BlendFunction blend, uint flags);

    [DllImport("user32.dll")]
    public static extern UIntPtr SetTimer(IntPtr hWnd, UIntPtr id, uint elapse, IntPtr timerProc);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool KillTimer(IntPtr hWnd, UIntPtr id);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out Msg message, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TranslateMessage(ref Msg message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref Msg message);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool TrackMouseEvent(ref TrackMouseEventData data);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCapture(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr GetCapture();

    [DllImport("user32.dll")]
    public static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    public static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll", EntryPoint = "TrackPopupMenuEx")]
    public static extern int TrackPopupMenuReturnCommand(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfoHeader header, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr gdiObject);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("ole32.dll")]
    public static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("ole32.dll")]
    public static extern void CoUninitialize();
}
