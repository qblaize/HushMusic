using HushMusic.App.Services.Performance;

namespace HushMusic.App.Helpers;

/// <summary>
/// Returns the memory of UI that was just let go of (a page dropped from the navigation cache, Now Playing closed).
/// XAML elements and bitmaps that have a managed wrapper (custom controls, named elements, bound items) are only
/// released when the garbage collector finalizes that wrapper. The app allocates little managed memory once a page has
/// loaded, so without a nudge those full collections, and the release of whole page trees, can wait for many minutes.
/// </summary>
internal static class UiMemory
{
    /// <summary>
    /// Collects a few seconds from now, on a thread-pool thread; repeated calls collect once, and a trim about to run
    /// (the window just hid) does it instead (<see cref="ProcessMemory"/>). Call it on the UI thread.
    /// </summary>
    public static void CollectSoon() => ProcessMemory.CollectSoon();
}
