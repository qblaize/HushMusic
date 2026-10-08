using System.Runtime.InteropServices;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// Finds Explorer's taskbars, the main one (Shell_TrayWnd) and one per other display (Shell_SecondaryTrayWnd, when
/// "Show my taskbar on all displays" is on), and lists the connected displays with their monitor names. Read-only
/// Win32 queries; any thread.
/// </summary>
internal static class TaskbarWindows
{
    public const string PrimaryClass = "Shell_TrayWnd";
    public const string SecondaryClass = "Shell_SecondaryTrayWnd";

    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorInfoPrimary = 1;
    private const uint QdcOnlyActivePaths = 2;
    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetTargetName = 2;
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    /// <summary>The taskbar on <paramref name="display"/> (a device name), or the main taskbar when it is null.</summary>
    public static IntPtr Find(string? display)
    {
        if (display is null)
        {
            return WidgetNative.FindWindow(PrimaryClass, null);
        }

        foreach (var taskbar in All())
        {
            if (TaskbarDisplayChoice.SameDevice(DisplayOf(taskbar), display))
            {
                return taskbar;
            }
        }

        return IntPtr.Zero;
    }

    /// <summary>Every taskbar window, the main one first.</summary>
    public static List<IntPtr> All()
    {
        var taskbars = new List<IntPtr>();
        var primary = WidgetNative.FindWindow(PrimaryClass, null);
        if (primary != IntPtr.Zero)
        {
            taskbars.Add(primary);
        }

        // A handful at most; the cap only guards against a window list that changes while it is walked.
        var secondary = IntPtr.Zero;
        for (var i = 0; i < 32; i++)
        {
            secondary = WidgetNative.FindWindowEx(IntPtr.Zero, secondary, SecondaryClass, null);
            if (secondary == IntPtr.Zero)
            {
                break;
            }

            taskbars.Add(secondary);
        }

        return taskbars;
    }

    /// <summary>The device name (e.g. <c>\\.\DISPLAY2</c>) of the display a window is on.</summary>
    public static string? DisplayOf(IntPtr window)
    {
        var monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        return monitor != IntPtr.Zero && Info(monitor) is { } info ? info.Device : null;
    }

    /// <summary>The connected displays in display-number order, with their monitor names where Windows knows them.</summary>
    public static IReadOnlyList<DisplayInfo> Displays()
    {
        var monitors = new List<IntPtr>();
        MonitorEnumProc collect = (monitor, _, _, _) =>
        {
            monitors.Add(monitor);
            return true;
        };
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, collect, IntPtr.Zero);
        GC.KeepAlive(collect);

        var names = MonitorNames();
        var displays = new List<DisplayInfo>();
        foreach (var monitor in monitors)
        {
            if (Info(monitor) is { } info && !displays.Any(d => TaskbarDisplayChoice.SameDevice(d.DeviceName, info.Device)))
            {
                displays.Add(new DisplayInfo(info.Device, names.GetValueOrDefault(info.Device), info.Primary));
            }
        }

        return [.. displays.OrderBy(d => TaskbarDisplayChoice.ShortName(d.DeviceName).Length).ThenBy(d => d.DeviceName, StringComparer.OrdinalIgnoreCase)];
    }

    private static (string Device, bool Primary)? Info(IntPtr monitor)
    {
        var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
        return GetMonitorInfo(monitor, ref info) && !string.IsNullOrEmpty(info.Device)
            ? (info.Device, (info.Flags & MonitorInfoPrimary) != 0)
            : null;
    }

    // GDI device name -> the monitor's EDID name ("DELL U2720Q"), as Settings → Display shows it. Best effort: an
    // empty map just leaves the names out.
    private static Dictionary<string, string> MonitorNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0 || pathCount == 0)
            {
                return names;
            }

            var paths = new byte[pathCount * PathInfoSize];
            var modes = new byte[Math.Max(1, modeCount) * ModeInfoSize];
            if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0)
            {
                return names;
            }

            for (var i = 0; i < pathCount; i++)
            {
                var path = paths.AsSpan(i * PathInfoSize, PathInfoSize);

                // DISPLAYCONFIG_PATH_INFO: sourceInfo (adapter LUID, id) at 0, targetInfo (adapter LUID, id) at 20.
                var source = new SourceDeviceName { Header = Header(DeviceInfoGetSourceName, Marshal.SizeOf<SourceDeviceName>(), path, 0) };
                var target = new TargetDeviceName { Header = Header(DeviceInfoGetTargetName, Marshal.SizeOf<TargetDeviceName>(), path, 20) };
                if (DisplayConfigGetDeviceInfo(ref source) == 0
                    && DisplayConfigGetDeviceInfo(ref target) == 0
                    && !string.IsNullOrWhiteSpace(target.MonitorFriendlyDeviceName))
                {
                    names.TryAdd(source.ViewGdiDeviceName, target.MonitorFriendlyDeviceName.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
        }

        return names;
    }

    private static DeviceInfoHeader Header(uint type, int size, ReadOnlySpan<byte> path, int offset) => new()
    {
        Type = type,
        Size = (uint)size,
        AdapterId = new Luid
        {
            LowPart = BitConverter.ToUInt32(path[offset..]),
            HighPart = BitConverter.ToInt32(path[(offset + 4)..]),
        },
        Id = BitConverter.ToUInt32(path[(offset + 8)..]),
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Win32.Rect Monitor;
        public Win32.Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        public DeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        public DeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string MonitorDevicePath;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hWnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, byte[] paths, ref uint modeCount, byte[] modes, IntPtr topologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref SourceDeviceName request);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref TargetDeviceName request);
}
