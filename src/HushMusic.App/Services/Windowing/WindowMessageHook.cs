using Microsoft.Extensions.Logging;

namespace HushMusic.App.Services.Windowing;

/// <summary>
/// Subclasses the main window (comctl32 SetWindowSubclass) to see messages WinUI doesn't surface: the taskbar's
/// "TaskbarButtonCreated" and thumbnail-toolbar clicks (WM_COMMAND). UI thread only.
/// </summary>
internal sealed class WindowMessageHook : IDisposable
{
    /// <summary>Returns true when the message was handled and must not reach the window.</summary>
    public delegate bool MessageHandler(uint message, IntPtr wParam, IntPtr lParam);

    private static readonly UIntPtr SubclassId = new(0x59544D);

    private readonly IntPtr _hwnd;
    private readonly MessageHandler _handler;
    private readonly ILogger _logger;

    // Kept in a field: the native side holds only a function pointer to this delegate.
    private readonly Win32.SubclassProc _proc;
    private bool _installed;

    public WindowMessageHook(IntPtr hwnd, MessageHandler handler, ILogger logger)
    {
        _hwnd = hwnd;
        _handler = handler;
        _logger = logger;
        _proc = WindowProc;
        _installed = Win32.SetWindowSubclass(hwnd, _proc, SubclassId, UIntPtr.Zero);
        if (!_installed)
        {
            _logger.LogWarning("Could not subclass the main window; taskbar buttons won't respond");
        }
    }

    public void Dispose()
    {
        if (_installed)
        {
            _installed = false;
            Win32.RemoveWindowSubclass(_hwnd, _proc, SubclassId);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr idSubclass, UIntPtr refData)
    {
        if (msg == Win32.WmNcDestroy)
        {
            Dispose();
            return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        try
        {
            if (_handler(msg, wParam, lParam))
            {
                return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            // An exception must never unwind into the native window procedure.
            _logger.LogError(ex, "Window message {Message:X} handler failed", msg);
        }

        return Win32.DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
