namespace HushMusic.App.Controls.Items;

/// <summary>One placeholder item: its lead block and the widths of its two text lines (varied so a list doesn't look ruled).</summary>
public sealed class SkeletonItem
{
    public double Primary { get; init; }

    public double Secondary { get; init; }

    public double LeadWidth { get; init; }

    public double LeadHeight { get; init; }

    /// <summary>Round art (artist cards).</summary>
    public bool IsRound { get; init; }
}

internal static class SkeletonData
{
    /// <summary>Deterministic, pleasantly uneven line widths (the same on every load, so nothing flickers).</summary>
    public static IReadOnlyList<SkeletonItem> Create(
        int count,
        (double Min, double Range) primary,
        (double Min, double Range) secondary,
        double leadWidth = 0,
        double leadHeight = 0,
        bool isRound = false)
    {
        var items = new SkeletonItem[Math.Max(0, count)];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = new SkeletonItem
            {
                Primary = primary.Min + (i * 53 % 100 / 100.0 * primary.Range),
                Secondary = secondary.Min + ((i * 37 + 41) % 100 / 100.0 * secondary.Range),
                LeadWidth = leadWidth,
                LeadHeight = leadHeight,
                IsRound = isRound,
            };
        }

        return items;
    }
}
