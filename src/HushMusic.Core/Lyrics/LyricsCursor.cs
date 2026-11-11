namespace HushMusic.Core.Lyrics;

/// <summary>One item of synced lyrics as a view lays them out: a sung line, or an instrumental break.</summary>
public interface ITimedLyric
{
    /// <summary>When the item starts.</summary>
    TimeSpan Start { get; }

    /// <summary>An instrumental break: nothing is sung until the next item.</summary>
    bool IsInstrumental { get; }
}

/// <summary>
/// What a small lyrics view (the mini player) shows: the item being sung, prominently, and the next sung line, dimmed.
/// </summary>
/// <param name="Current">Index of the item being sung; -1 before the first one.</param>
/// <param name="Next">Index of the next sung line after <paramref name="Current"/> (breaks skipped); -1 when none follows.</param>
/// <param name="IsPause">Nothing is sung now: before the first line, or an instrumental break.</param>
public readonly record struct LyricsGlance(int Current, int Next, bool IsPause)
{
    /// <summary>No lyrics.</summary>
    public static LyricsGlance None { get; } = new(-1, -1, IsPause: false);

    /// <summary>
    /// This glance is the one after <paramref name="previous"/> as the song plays on (one item later), not a jump or the
    /// first glance at new lyrics.
    /// </summary>
    public bool Follows(LyricsGlance previous) => previous != None && Current >= 0 && Current == previous.Current + 1;
}

/// <summary>Where playback is in synced lyrics: the item being sung, when the next one starts, and what a small view shows.</summary>
public static class LyricsCursor
{
    /// <summary>The last item that has started at <paramref name="position"/> (items in time order); -1 before the first.</summary>
    public static int IndexAt(IReadOnlyList<ITimedLyric> lines, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(lines);
        int low = 0, high = lines.Count - 1, index = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Start <= position)
            {
                index = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return index;
    }

    /// <summary>
    /// How long from <paramref name="position"/> until the item after <paramref name="current"/> starts (negative when it
    /// is already due); null when <paramref name="current"/> is the last item.
    /// </summary>
    public static TimeSpan? UntilNext(IReadOnlyList<ITimedLyric> lines, int current, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var next = Math.Max(current, -1) + 1;
        return next < lines.Count ? lines[next].Start - position : null;
    }

    /// <summary>The glance at <paramref name="current"/> (an index from <see cref="IndexAt"/>).</summary>
    public static LyricsGlance Glance(IReadOnlyList<ITimedLyric> lines, int current)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return LyricsGlance.None;
        }

        current = Math.Clamp(current, -1, lines.Count - 1);
        var next = -1;
        for (var i = current + 1; i < lines.Count; i++)
        {
            if (!lines[i].IsInstrumental)
            {
                next = i;
                break;
            }
        }

        return new LyricsGlance(current, next, current < 0 || lines[current].IsInstrumental);
    }
}
