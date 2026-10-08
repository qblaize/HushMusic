namespace HushMusic.Core.Services;

/// <summary>A rectangle in whole pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;

    public int Bottom => Y + Height;

    public bool IsEmpty => Width <= 0 || Height <= 0;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };
}

/// <summary>A horizontal span [<see cref="Left"/>, <see cref="Right"/>) in pixels.</summary>
public readonly record struct PixelSpan(int Left, int Right)
{
    public int Width => Right - Left;
}

/// <summary>The parts of the taskbar player that react to the mouse.</summary>
public enum TaskbarWidgetPart
{
    None,

    /// <summary>Cover and text: opens the flyout.</summary>
    Content,
    Previous,
    PlayPause,
    Next,
}

/// <summary>What is on a horizontal taskbar, in taskbar-client pixels.</summary>
/// <param name="Width">Taskbar width.</param>
/// <param name="Height">Taskbar height.</param>
/// <param name="Scale">DPI scale (1.0 at 96 DPI).</param>
/// <param name="Icons">The Start button and the app buttons. Null when unknown.</param>
/// <param name="TrayLeft">Left edge of the notification area (or <paramref name="Width"/>).</param>
/// <param name="Occupied">Anything else drawn on the taskbar (Widgets button, mods).</param>
public sealed record TaskbarContent(int Width, int Height, double Scale, PixelSpan? Icons, int TrayLeft, IReadOnlyList<PixelSpan> Occupied);

/// <summary>Where everything sits inside the taskbar player, in widget pixels.</summary>
public sealed record TaskbarWidgetGeometry(
    int Width,
    int Height,
    double Scale,
    PixelRect Highlight,
    int CornerRadius,
    PixelRect Cover,
    int CoverRadius,
    PixelRect Text,
    PixelRect Previous,
    PixelRect PlayPause,
    PixelRect Next,
    int ButtonRadius,
    PixelRect Progress);

/// <summary>
/// Layout of the taskbar player: where it fits on the taskbar, where its parts go, what the mouse is over and how the
/// wheel changes the volume. Pure logic in pixels (the window and the drawing live in the App).
/// </summary>
public static class TaskbarWidgetLayout
{
    /// <summary>Preferred width in DIPs.</summary>
    public const double PreferredWidth = 288;

    /// <summary>Below this width (DIPs) the text gets too short to be useful, so the player hides.</summary>
    public const double MinimumWidth = 200;

    /// <summary>Volume change per wheel notch, in percent.</summary>
    public const int VolumeStep = 5;

    /// <summary>One wheel notch (WHEEL_DELTA).</summary>
    public const int WheelNotch = 120;

    private const double EdgeMargin = 4;
    private const double Gap = 12;

    /// <summary>
    /// The player's rectangle on the taskbar, or null when it doesn't fit. Centred icons: the leftmost free space left of
    /// the icons (the left end of the taskbar). Left-aligned icons: the rightmost free space before the notification area.
    /// </summary>
    public static PixelRect? Place(TaskbarContent taskbar)
    {
        if (taskbar.Icons is not { } icons || taskbar.Width <= 0 || taskbar.Height <= 0 || taskbar.Height > taskbar.Width)
        {
            return null;
        }

        var scale = taskbar.Scale > 0 ? taskbar.Scale : 1;
        var edge = Px(EdgeMargin, scale);
        var gap = Px(Gap, scale);
        var preferred = Px(PreferredWidth, scale);
        var minimum = Px(MinimumWidth, scale);
        var blocked = taskbar.Occupied.Select(o => new PixelSpan(o.Left - gap, o.Right + gap)).ToList();

        var left = Free(new PixelSpan(edge, icons.Left - gap), blocked).FirstOrDefault(s => s.Width >= minimum);
        if (left.Width >= minimum)
        {
            return new PixelRect(left.Left, 0, Math.Min(preferred, left.Width), taskbar.Height);
        }

        var trayLeft = Math.Min(taskbar.TrayLeft, taskbar.Width - edge);
        var right = Free(new PixelSpan(icons.Right + gap, trayLeft - gap), blocked).LastOrDefault(s => s.Width >= minimum);
        if (right.Width >= minimum)
        {
            var width = Math.Min(preferred, right.Width);
            return new PixelRect(right.Right - width, 0, width, taskbar.Height);
        }

        return null;
    }

    /// <summary>
    /// Splits what the taskbar draws into the icon group (the Start button and every button next to it, gaps up to
    /// <paramref name="maxGap"/>) and everything else (Widgets button, mods). Spans may overlap or nest.
    /// </summary>
    public static (PixelSpan Icons, IReadOnlyList<PixelSpan> Others) SplitIcons(IReadOnlyList<PixelSpan> spans, PixelSpan start, int maxGap)
    {
        var icons = start;
        var rest = spans.Where(s => s != start).ToList();
        for (var grew = true; grew;)
        {
            grew = false;
            for (var i = 0; i < rest.Count; i++)
            {
                if (rest[i].Left <= icons.Right + maxGap && rest[i].Right >= icons.Left - maxGap)
                {
                    icons = new PixelSpan(Math.Min(icons.Left, rest[i].Left), Math.Max(icons.Right, rest[i].Right));
                    rest.RemoveAt(i);
                    grew = true;
                    break;
                }
            }
        }

        return (icons, rest);
    }

    /// <summary>
    /// [cover] [title / artist] [previous] [play-pause] [next], with a hover highlight like the taskbar's buttons and a
    /// progress line along its bottom edge.
    /// </summary>
    public static TaskbarWidgetGeometry Measure(int width, int height, double scale)
    {
        scale = scale > 0 ? scale : 1;
        var highlightHeight = Math.Min(height, Px(40, scale));
        var highlight = new PixelRect(0, (height - highlightHeight) / 2, width, highlightHeight);
        var inset = Px(4, scale);

        var coverSize = Math.Max(1, Math.Min(Px(32, scale), highlightHeight - (2 * inset)));
        var cover = new PixelRect(inset, highlight.Y + ((highlightHeight - coverSize) / 2), coverSize, coverSize);

        var buttonWidth = Px(32, scale);
        var buttonHeight = Math.Min(Px(32, scale), highlightHeight);
        var buttonY = highlight.Y + ((highlightHeight - buttonHeight) / 2);
        var next = new PixelRect(width - inset - buttonWidth, buttonY, buttonWidth, buttonHeight);
        var playPause = next with { X = next.X - buttonWidth };
        var previous = playPause with { X = playPause.X - buttonWidth };

        var textLeft = cover.Right + Px(8, scale);
        var text = new PixelRect(textLeft, highlight.Y, Math.Max(0, previous.X - Px(4, scale) - textLeft), highlightHeight);

        // Under the text column, just below the cover's bottom edge.
        var progressHeight = Math.Max(1, Px(2, scale));
        var progress = new PixelRect(textLeft, Math.Min(cover.Bottom + Px(1, scale), highlight.Bottom - progressHeight), text.Width, progressHeight);

        return new TaskbarWidgetGeometry(
            width,
            height,
            scale,
            highlight,
            Px(4, scale),
            cover,
            Px(4, scale),
            text,
            previous,
            playPause,
            next,
            Px(4, scale),
            progress);
    }

    /// <summary>The part under the point (widget pixels). The buttons take the full height of their column.</summary>
    public static TaskbarWidgetPart HitTest(TaskbarWidgetGeometry geometry, int x, int y)
    {
        if (x < 0 || y < 0 || x >= geometry.Width || y >= geometry.Height)
        {
            return TaskbarWidgetPart.None;
        }

        if (x >= geometry.Previous.X && x < geometry.Previous.Right)
        {
            return TaskbarWidgetPart.Previous;
        }

        if (x >= geometry.PlayPause.X && x < geometry.PlayPause.Right)
        {
            return TaskbarWidgetPart.PlayPause;
        }

        if (x >= geometry.Next.X && x < geometry.Next.Right)
        {
            return TaskbarWidgetPart.Next;
        }

        return x < geometry.Previous.X ? TaskbarWidgetPart.Content : TaskbarWidgetPart.None;
    }

    /// <summary>
    /// The volume (0 – 100) after <paramref name="notches"/> wheel notches: moves to the next multiple of
    /// <see cref="VolumeStep"/> in that direction, then a step per further notch.
    /// </summary>
    public static double StepVolume(double volume, int notches)
    {
        var current = Math.Clamp(double.IsFinite(volume) ? volume : 0, 0, 100);
        if (notches == 0)
        {
            return current;
        }

        var snapped = notches > 0
            ? (Math.Floor((current + 0.001) / VolumeStep) + 1) * VolumeStep
            : (Math.Ceiling((current - 0.001) / VolumeStep) - 1) * VolumeStep;
        var target = snapped + ((notches - Math.Sign(notches)) * VolumeStep);
        return Math.Clamp(target, 0, 100);
    }

    /// <summary>
    /// Adds a WM_MOUSEWHEEL delta (precision touchpads send fractions of a notch) and returns the whole notches it
    /// completes; the rest stays in <paramref name="pending"/>. Turning the other way drops what was pending.
    /// </summary>
    public static int TakeNotches(ref int pending, int delta)
    {
        if (pending != 0 && Math.Sign(pending) != Math.Sign(delta))
        {
            pending = 0;
        }

        pending += delta;
        var notches = pending / WheelNotch;
        pending -= notches * WheelNotch;
        return notches;
    }

    private static int Px(double dips, double scale) => (int)Math.Round(dips * scale);

    // The parts of `range` not covered by any of `blocked`, left to right.
    private static IEnumerable<PixelSpan> Free(PixelSpan range, IReadOnlyList<PixelSpan> blocked)
    {
        var start = range.Left;
        foreach (var block in blocked.Where(b => b.Right > range.Left && b.Left < range.Right).OrderBy(b => b.Left))
        {
            if (block.Left > start)
            {
                yield return new PixelSpan(start, block.Left);
            }

            start = Math.Max(start, block.Right);
        }

        if (range.Right > start)
        {
            yield return new PixelSpan(start, range.Right);
        }
    }
}
