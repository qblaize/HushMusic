using System.Runtime.InteropServices;

namespace HushMusic.App.Services.Windowing;

public enum TaskbarProgressState
{
    None = 0,
    Indeterminate = 0x1,
    Normal = 0x2,
    Error = 0x4,
    Paused = 0x8,
}

/// <summary>
/// The shell's ITaskbarList3/4 (thumbnail toolbar and taskbar progress), called through its vtable. Built-in COM
/// interop is unavailable in trimmed builds, so there are no [ComImport] interfaces. STA (UI) thread only.
/// </summary>
internal sealed class TaskbarList : IDisposable
{
    // ITaskbarList3 vtable slots: IUnknown 0-2, ITaskbarList 3-7, ITaskbarList2 8, ITaskbarList3 9-20.
    private const int HrInitSlot = 3;
    private const int SetProgressValueSlot = 9;
    private const int SetProgressStateSlot = 10;
    private const int ThumbBarAddButtonsSlot = 15;
    private const int ThumbBarUpdateButtonsSlot = 16;

    private IntPtr _instance;
    private readonly SetProgressValueFn _setProgressValue;
    private readonly SetProgressStateFn _setProgressState;
    private readonly ThumbBarButtonsFn _addButtons;
    private readonly ThumbBarButtonsFn _updateButtons;

    private TaskbarList(IntPtr instance)
    {
        _instance = instance;
        _setProgressValue = Method<SetProgressValueFn>(SetProgressValueSlot);
        _setProgressState = Method<SetProgressStateFn>(SetProgressStateSlot);
        _addButtons = Method<ThumbBarButtonsFn>(ThumbBarAddButtonsSlot);
        _updateButtons = Method<ThumbBarButtonsFn>(ThumbBarUpdateButtonsSlot);
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int HrInitFn(IntPtr self);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetProgressValueFn(IntPtr self, IntPtr hwnd, ulong completed, ulong total);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int SetProgressStateFn(IntPtr self, IntPtr hwnd, int flags);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ThumbBarButtonsFn(IntPtr self, IntPtr hwnd, uint count, [In] Win32.ThumbButton[] buttons);

    /// <summary>Creates and initializes the taskbar object; returns null with the HRESULT when the shell refuses.</summary>
    public static TaskbarList? TryCreate(out int hresult)
    {
        var clsid = new Guid("56FDF344-FD6D-11d0-958A-006097C9A090");
        // ITaskbarList4 (Windows 7+): derives from ITaskbarList3, so slots 9-20 are the same.
        var iid = new Guid("C43DC798-95D1-4BEA-9030-BB99E2983A1A");
        hresult = Win32.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out var instance);
        if (hresult < 0 || instance == IntPtr.Zero)
        {
            return null;
        }

        var list = new TaskbarList(instance);
        hresult = list.Method<HrInitFn>(HrInitSlot)(instance);
        if (hresult < 0)
        {
            list.Dispose();
            return null;
        }

        return list;
    }

    public int SetProgressValue(IntPtr hwnd, ulong completed, ulong total) =>
        _instance == IntPtr.Zero ? -1 : _setProgressValue(_instance, hwnd, completed, total);

    public int SetProgressState(IntPtr hwnd, TaskbarProgressState state) =>
        _instance == IntPtr.Zero ? -1 : _setProgressState(_instance, hwnd, (int)state);

    public int AddThumbButtons(IntPtr hwnd, Win32.ThumbButton[] buttons) =>
        _instance == IntPtr.Zero ? -1 : _addButtons(_instance, hwnd, (uint)buttons.Length, buttons);

    public int UpdateThumbButtons(IntPtr hwnd, Win32.ThumbButton[] buttons) =>
        _instance == IntPtr.Zero ? -1 : _updateButtons(_instance, hwnd, (uint)buttons.Length, buttons);

    public void Dispose()
    {
        if (_instance != IntPtr.Zero)
        {
            Marshal.Release(_instance);
            _instance = IntPtr.Zero;
        }
    }

    private T Method<T>(int slot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(_instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }
}
