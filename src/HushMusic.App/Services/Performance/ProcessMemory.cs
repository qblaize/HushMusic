using System.Runtime;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace HushMusic.App.Services.Performance;

/// <summary>
/// Gives memory back to Windows. Two kinds of request, coordinated here so they never pile up into back-to-back full
/// collections:
/// <list type="bullet">
/// <item><see cref="CollectSoon"/>: a full collection once the UI has let go of something (a page, Now Playing, the
/// artwork of a hidden window). Requests within a few seconds of each other collect once.</item>
/// <item><see cref="TrimSoon"/>: a compacting collection plus an emptied working set, for while nothing is on screen
/// (the window service asks for one when the window hides or stays minimized). A collection due before it is folded
/// into it.</item>
/// </list>
/// Call the scheduling methods on the UI thread; the work runs on a thread-pool thread.
/// </summary>
public static class ProcessMemory
{
    // Once a navigation or a closing animation has settled; repeated requests within it collect once.
    private static readonly TimeSpan CollectDelay = TimeSpan.FromSeconds(3);

    // A collection asked for this close to a pending trim is left to the trim.
    private static readonly TimeSpan FoldWindow = TimeSpan.FromSeconds(10);

    private static readonly Lock Gate = new();
    private static CancellationTokenSource? s_collect;
    private static CancellationTokenSource? s_trim;
    private static DateTime s_trimDue;
    private static bool s_collectFolded;

    /// <summary>
    /// Collects a few seconds from now: XAML elements and bitmaps that have a managed wrapper are only released when the
    /// garbage collector finalizes it, and once the UI has loaded the app allocates too little for that to happen soon.
    /// </summary>
    public static void CollectSoon()
    {
        lock (Gate)
        {
            if (s_trim is not null && s_trimDue - DateTime.UtcNow <= FoldWindow)
            {
                s_collectFolded = true;
                return;
            }

            s_collect?.Cancel();
            s_collect = new CancellationTokenSource();
            _ = RunAfterAsync(CollectDelay, s_collect, trim: false, logger: null);
        }
    }

    /// <summary>
    /// Trims after <paramref name="delay"/> unless <see cref="CancelTrim"/> comes first; replaces a pending trim. Ask for it
    /// when nothing is on screen: pages still in use come back on their next touch.
    /// </summary>
    public static void TrimSoon(TimeSpan delay, ILogger? logger = null)
    {
        lock (Gate)
        {
            s_trim?.Cancel();
            s_trim = new CancellationTokenSource();
            s_trimDue = DateTime.UtcNow + delay;
            if (delay <= FoldWindow && s_collect is not null)
            {
                s_collect.Cancel();
                s_collect = null;
                s_collectFolded = true;
            }

            _ = RunAfterAsync(delay, s_trim, trim: true, logger);
        }
    }

    /// <summary>Drops a pending trim (the window is back on screen); a collection folded into it still happens.</summary>
    public static void CancelTrim()
    {
        bool collect;
        lock (Gate)
        {
            if (s_trim is null)
            {
                return;
            }

            s_trim.Cancel();
            s_trim = null;
            collect = s_collectFolded;
            s_collectFolded = false;
        }

        if (collect)
        {
            CollectSoon();
        }
    }

    /// <summary>
    /// A full, compacting collection (the large object heap included) that also decommits the heap's free space, then
    /// an emptied working set. Blocks for up to a few hundred milliseconds; never call it on the UI thread (the
    /// finalizers it waits for may need it).
    /// </summary>
    public static void Trim()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // WinRT objects (bitmaps, XAML elements) held only by a managed wrapper are released by its finalizer;
        // a second collection frees what those finalizers let go of.
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        // (-1, -1): remove as many pages as possible from the working set.
        SetProcessWorkingSetSizeEx(GetCurrentProcess(), -1, -1, 0);
    }

    private static void Collect()
    {
        // Not compacting: the managed heap is small; the native memory behind the wrappers is what this is for.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
    }

    private static async Task RunAfterAsync(TimeSpan delay, CancellationTokenSource cts, bool trim, ILogger? logger)
    {
        try
        {
            await Task.Delay(delay, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (Gate)
        {
            if (cts.IsCancellationRequested)
            {
                return;
            }

            if (trim)
            {
                s_trim = null;
                s_collectFolded = false;

                // The trim collects too.
                s_collect?.Cancel();
                s_collect = null;
            }
            else
            {
                s_collect = null;
            }
        }

        if (trim)
        {
            var before = Environment.WorkingSet;
            Trim();
            logger?.LogDebug(
                "Memory trimmed: working set {Before:N0} -> {After:N0} MB",
                before / (1024 * 1024),
                Environment.WorkingSet / (1024 * 1024));
        }
        else
        {
            Collect();
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize, uint flags);
}
