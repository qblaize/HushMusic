using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// Reads what shows behind the taskbar player (<see cref="TaskbarBackdrop"/>): copies the screen in its rectangle and
/// measures the pixels it leaves clear. Runs on the thread pool, because a screen read waits for the compositor and the
/// widget thread must never wait (Explorer's taskbar waits on it). Posts <paramref name="doneMessage"/> to
/// <paramref name="notify"/> with each answer. Only the measurement is kept; the copied pixels are dropped at once.
/// </summary>
internal sealed class TaskbarBackdropSampler(IntPtr notify, uint doneMessage, ILogger logger)
{
    private const uint SrcCopy = 0x00CC0020;

    private int _running;
    private volatile SampleResult? _latest;

    /// <summary>The most recent answer (any thread).</summary>
    public SampleResult? Latest => _latest;

    /// <summary>
    /// Samples <paramref name="screenRect"/> soon, leaving out where <paramref name="overlay"/> (the player's current
    /// premultiplied pixels, the rectangle's size) draws. Ignored while a sample runs; the next check asks again.
    /// </summary>
    public void Request(Win32.Rect screenRect, byte[] overlay)
    {
        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        ThreadPool.UnsafeQueueUserWorkItem(_ => Run(screenRect, overlay), null);
    }

    private void Run(Win32.Rect rect, byte[] overlay)
    {
        try
        {
            var clock = Stopwatch.StartNew();
            var screen = Capture(rect);
            var reading = screen is null ? null : TaskbarBackdrop.Read(screen, overlay);
            _latest = new SampleResult(reading, clock.Elapsed.TotalMilliseconds);
            Win32.PostMessage(notify, doneMessage, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Reading the taskbar player's backdrop failed");
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    // BGRA rows of the screen in the rectangle, as the compositor last showed it.
    private static byte[]? Capture(Win32.Rect rect)
    {
        var width = rect.Right - rect.Left;
        var height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var screen = GetDC(IntPtr.Zero);
        if (screen == IntPtr.Zero)
        {
            return null;
        }

        var memory = IntPtr.Zero;
        var dib = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memory = WidgetNative.CreateCompatibleDC(screen);
            var header = new WidgetNative.BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<WidgetNative.BitmapInfoHeader>(),
                Width = width,
                Height = -height, // top-down
                Planes = 1,
                BitCount = 32,
            };
            dib = WidgetNative.CreateDIBSection(memory, ref header, 0 /* DIB_RGB_COLORS */, out var bits, IntPtr.Zero, 0);
            if (memory == IntPtr.Zero || dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                return null;
            }

            previous = WidgetNative.SelectObject(memory, dib);
            if (!BitBlt(memory, 0, 0, width, height, screen, rect.Left, rect.Top, SrcCopy))
            {
                return null;
            }

            GdiFlush();
            var pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);
            return pixels;
        }
        finally
        {
            if (previous != IntPtr.Zero)
            {
                WidgetNative.SelectObject(memory, previous);
            }

            if (dib != IntPtr.Zero)
            {
                WidgetNative.DeleteObject(dib);
            }

            if (memory != IntPtr.Zero)
            {
                WidgetNative.DeleteDC(memory);
            }

            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}

/// <summary>A backdrop sample: the reading (null when too little of the player's rectangle was clear) and its cost.</summary>
internal sealed record SampleResult(BackdropReading? Reading, double Milliseconds);
