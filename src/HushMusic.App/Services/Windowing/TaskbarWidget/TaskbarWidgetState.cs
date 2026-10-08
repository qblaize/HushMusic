using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// What the taskbar player shows: an immutable snapshot built on the UI thread and handed to the widget thread.
/// </summary>
/// <param name="HasTrack">False: nothing loaded, the player hides.</param>
/// <param name="Progress">0 – 1, or null when there is no known duration (live streams, not loaded yet).</param>
/// <param name="IsLive">A live stream: shows "LIVE" instead of the progress line.</param>
/// <param name="Volume">0 – 100.</param>
/// <param name="Accent">The app accent as 0xAARRGGBB, before taskbar tuning.</param>
internal sealed record TaskbarWidgetState(
    bool HasTrack,
    string Title,
    string Artist,
    bool IsPlaying,
    double? Progress,
    bool IsLive,
    double Volume,
    bool IsMuted,
    uint Accent,
    WidgetArt? Art)
{
    public static TaskbarWidgetState Empty { get; } = new(false, string.Empty, string.Empty, false, null, false, 0, false, 0xFF9D7BFA, null);
}

/// <summary>Album art decoded to premultiplied BGRA at <see cref="Size"/> x <see cref="Size"/>, corners already rounded.</summary>
internal sealed record WidgetArt(string Url, int Size, byte[] Pixels);

/// <summary>Mouse and menu actions of the taskbar player, sent to the app.</summary>
internal enum TaskbarWidgetCommand
{
    Previous,
    PlayPause,
    Next,
    OpenApp,
    HidePlayer,
}

/// <summary>Where the player sits on screen, for placing the flyout above it. Physical pixels.</summary>
internal readonly record struct TaskbarWidgetAnchor(PixelRect Widget, PixelRect Taskbar, uint Dpi);

/// <summary>
/// The app side of the taskbar player. Called on the widget thread: implementations only hand the work to the UI
/// thread and return at once (that thread's input queue is shared with Explorer's taskbar).
/// </summary>
internal interface ITaskbarWidgetSink
{
    void OnCommand(TaskbarWidgetCommand command);

    /// <summary>Wheel notches over the player (positive = up).</summary>
    void OnVolumeNotches(int notches);

    /// <summary>The cover or text was clicked.</summary>
    void OnToggleFlyout(TaskbarWidgetAnchor anchor);

    /// <summary>The cover is drawn at this many pixels now (DPI change); the art should be decoded at that size.</summary>
    void OnCoverSizeChanged(int pixels);

    /// <summary>Displays were added, removed or rearranged (WM_DISPLAYCHANGE).</summary>
    void OnDisplaysChanged();
}
