using Windows.Foundation;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>Lays children out in <see cref="Columns"/> equal columns that always fill the width (related cards).</summary>
public sealed partial class CardGrid : Panel
{
    public int Columns { get; set; } = 3;

    public double ColumnSpacing { get; set; } = 12;

    public double RowSpacing { get; set; } = 16;

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = ColumnWidth(availableSize.Width);
        double height = 0, rowHeight = 0;
        for (var i = 0; i < Children.Count; i++)
        {
            Children[i].Measure(new Size(width, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            if (i % Columns == Columns - 1 || i == Children.Count - 1)
            {
                height += rowHeight + (height > 0 ? RowSpacing : 0);
                rowHeight = 0;
            }
        }

        return new Size(double.IsInfinity(availableSize.Width) ? (width * Columns) + (ColumnSpacing * (Columns - 1)) : availableSize.Width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var width = ColumnWidth(finalSize.Width);
        double y = 0;
        for (var row = 0; row * Columns < Children.Count; row++)
        {
            var first = row * Columns;
            var last = Math.Min(Children.Count, first + Columns);
            double rowHeight = 0;
            for (var i = first; i < last; i++)
            {
                rowHeight = Math.Max(rowHeight, Children[i].DesiredSize.Height);
            }

            for (var i = first; i < last; i++)
            {
                Children[i].Arrange(new Rect((i - first) * (width + ColumnSpacing), y, width, rowHeight));
            }

            y += rowHeight + RowSpacing;
        }

        return finalSize;
    }

    private double ColumnWidth(double available) =>
        double.IsInfinity(available) ? 96 : Math.Max(0, (available - (ColumnSpacing * (Columns - 1))) / Columns);
}

/// <summary>A grid as tall as it is wide (card artwork that scales with its column).</summary>
public sealed partial class SquareGrid : Grid
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var side = double.IsInfinity(availableSize.Width) ? 96 : availableSize.Width;
        base.MeasureOverride(new Size(side, side));
        return new Size(side, side);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var side = finalSize.Width;
        base.ArrangeOverride(new Size(side, side));
        return new Size(side, side);
    }
}
