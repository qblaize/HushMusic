using System.Runtime.InteropServices;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>One thing the Windows 11 taskbar draws (Start, an app button, the Widgets button, a mod), in screen pixels.</summary>
internal readonly record struct TaskbarElement(string? AutomationId, PixelRect Bounds);

/// <summary>
/// Reads the Windows 11 taskbar's layout through UI Automation, read-only: the XAML taskbar has no HWNDs for its
/// buttons, so this is the only way to see where the Start button is. Called through the vtables (like
/// <see cref="TaskbarList"/>): built-in COM interop is unavailable in trimmed builds. One MTA thread only: calls go
/// to Explorer and may block, so never the UI thread or the widget thread.
/// </summary>
internal sealed class TaskbarAutomation : IDisposable
{
    private const int AutomationIdProperty = 30011; // UIA_AutomationIdPropertyId
    private const int TreeScopeChildren = 2;
    private const int TreeScopeDescendants = 4;
    private const ushort VtBstr = 8;

    // IUIAutomation slots.
    private const int ElementFromHandleSlot = 6;
    private const int CreateTrueConditionSlot = 21;
    private const int CreatePropertyConditionSlot = 23;

    // IUIAutomationElement slots.
    private const int FindFirstSlot = 5;
    private const int FindAllSlot = 6;
    private const int CurrentAutomationIdSlot = 29;
    private const int CurrentBoundingRectangleSlot = 43;

    // IUIAutomationElementArray slots.
    private const int LengthSlot = 3;
    private const int GetElementSlot = 4;

    private static readonly Guid ClsidCUIAutomation = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private static readonly Guid IidIUIAutomation = new("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee");

    private IntPtr _automation;
    private IntPtr _trueCondition;
    private IntPtr _frameCondition;
    private IntPtr _frame;
    private IntPtr _frameOwner;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int ElementFromHandleFn(IntPtr self, IntPtr hwnd, out IntPtr element);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTrueConditionFn(IntPtr self, out IntPtr condition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreatePropertyConditionFn(IntPtr self, int propertyId, Variant value, out IntPtr condition);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int FindFn(IntPtr self, int scope, IntPtr condition, out IntPtr found);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetBstrFn(IntPtr self, out IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetRectFn(IntPtr self, out Win32.Rect value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetLengthFn(IntPtr self, out int length);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetElementFn(IntPtr self, int index, out IntPtr element);

    /// <summary>
    /// The visible children of the taskbar frame. Returns null when UI Automation or the frame is unavailable (older
    /// taskbars, Explorer restarting); the caller then falls back to the legacy HWNDs.
    /// </summary>
    public IReadOnlyList<TaskbarElement>? Query(IntPtr taskbar)
    {
        if (!EnsureAutomation() || !EnsureFrame(taskbar))
        {
            return null;
        }

        if (Call<FindFn>(_frame, FindAllSlot)(_frame, TreeScopeChildren, _trueCondition, out var array) < 0 || array == IntPtr.Zero)
        {
            // The cached frame died with its Explorer; find it again next time.
            ReleaseFrame();
            return null;
        }

        try
        {
            if (Call<GetLengthFn>(array, LengthSlot)(array, out var count) < 0)
            {
                return null;
            }

            var elements = new List<TaskbarElement>(count);
            for (var i = 0; i < count; i++)
            {
                if (Call<GetElementFn>(array, GetElementSlot)(array, i, out var element) < 0 || element == IntPtr.Zero)
                {
                    continue;
                }

                try
                {
                    if (BoundsOf(element) is { IsEmpty: false } bounds)
                    {
                        elements.Add(new TaskbarElement(AutomationIdOf(element), bounds));
                    }
                }
                finally
                {
                    Marshal.Release(element);
                }
            }

            return elements;
        }
        finally
        {
            Marshal.Release(array);
        }
    }

    public void Dispose()
    {
        ReleaseFrame();
        Release(ref _frameCondition);
        Release(ref _trueCondition);
        Release(ref _automation);
    }

    private static T Call<T>(IntPtr instance, int slot)
        where T : Delegate
    {
        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    private static void Release(ref IntPtr pointer)
    {
        if (pointer != IntPtr.Zero)
        {
            Marshal.Release(pointer);
            pointer = IntPtr.Zero;
        }
    }

    private static PixelRect? BoundsOf(IntPtr element)
    {
        if (Call<GetRectFn>(element, CurrentBoundingRectangleSlot)(element, out var r) < 0)
        {
            return null;
        }

        return new PixelRect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    private static string? AutomationIdOf(IntPtr element)
    {
        if (Call<GetBstrFn>(element, CurrentAutomationIdSlot)(element, out var bstr) < 0 || bstr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringBSTR(bstr);
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }
    }

    private bool EnsureAutomation()
    {
        if (_automation != IntPtr.Zero)
        {
            return true;
        }

        var clsid = ClsidCUIAutomation;
        var iid = IidIUIAutomation;
        if (Win32.CoCreateInstance(ref clsid, IntPtr.Zero, 1 /* CLSCTX_INPROC_SERVER */, ref iid, out _automation) < 0 || _automation == IntPtr.Zero)
        {
            _automation = IntPtr.Zero;
            return false;
        }

        var bstr = Marshal.StringToBSTR("TaskbarFrame");
        try
        {
            var value = new Variant { Type = VtBstr, Value = bstr };
            if (Call<CreateTrueConditionFn>(_automation, CreateTrueConditionSlot)(_automation, out _trueCondition) < 0
                || Call<CreatePropertyConditionFn>(_automation, CreatePropertyConditionSlot)(_automation, AutomationIdProperty, value, out _frameCondition) < 0)
            {
                Dispose();
                return false;
            }
        }
        finally
        {
            Marshal.FreeBSTR(bstr);
        }

        return true;
    }

    private bool EnsureFrame(IntPtr taskbar)
    {
        if (_frame != IntPtr.Zero && _frameOwner == taskbar)
        {
            return true;
        }

        ReleaseFrame();
        if (Call<ElementFromHandleFn>(_automation, ElementFromHandleSlot)(_automation, taskbar, out var root) < 0 || root == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (Call<FindFn>(root, FindFirstSlot)(root, TreeScopeDescendants, _frameCondition, out _frame) < 0 || _frame == IntPtr.Zero)
            {
                _frame = IntPtr.Zero;
                return false;
            }

            _frameOwner = taskbar;
            return true;
        }
        finally
        {
            Marshal.Release(root);
        }
    }

    private void ReleaseFrame()
    {
        Release(ref _frame);
        _frameOwner = IntPtr.Zero;
    }

    // VARIANT: 16 bytes on 32-bit, 24 on 64-bit (the union holds two pointers).
    [StructLayout(LayoutKind.Sequential)]
    private struct Variant
    {
        public ushort Type;
        public ushort Reserved1;
        public ushort Reserved2;
        public ushort Reserved3;
        public IntPtr Value;
        public IntPtr Extra;
    }
}
