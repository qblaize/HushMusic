using Windows.Foundation;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// One trimmable element (the first child, usually a title TextBlock) followed by small trailing elements
/// (an explicit badge) that sit right after the text instead of at the far edge. The text gets whatever
/// width the trailing elements leave, so it trims with an ellipsis while the badge stays visible.
/// </summary>
public sealed partial class TrailingPanel : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(TrailingPanel), new PropertyMetadata(6.0, (d, _) => ((TrailingPanel)d).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double trailing = 0, height = 0;
        for (var i = 1; i < Children.Count; i++)
        {
            var child = Children[i];
            child.Measure(new Size(double.PositiveInfinity, availableSize.Height));
            trailing += Occupied(child);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        if (Children.Count == 0)
        {
            return new Size(trailing, height);
        }

        var first = Children[0];
        var textWidth = double.IsPositiveInfinity(availableSize.Width) ? double.PositiveInfinity : Math.Max(0, availableSize.Width - trailing);
        first.Measure(new Size(textWidth, availableSize.Height));
        return new Size(first.DesiredSize.Width + trailing, Math.Max(height, first.DesiredSize.Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Children.Count == 0)
        {
            return finalSize;
        }

        double trailing = 0;
        for (var i = 1; i < Children.Count; i++)
        {
            trailing += Occupied(Children[i]);
        }

        var first = Children[0];
        var x = Math.Min(first.DesiredSize.Width, Math.Max(0, finalSize.Width - trailing));
        first.Arrange(new Rect(0, 0, x, finalSize.Height));

        for (var i = 1; i < Children.Count; i++)
        {
            var child = Children[i];
            var size = child.DesiredSize;
            if (size.Width <= 0)
            {
                child.Arrange(new Rect(x, 0, 0, 0));
                continue;
            }

            x += Spacing;
            child.Arrange(new Rect(x, Math.Max(0, (finalSize.Height - size.Height) / 2), size.Width, size.Height));
            x += size.Width;
        }

        return finalSize;
    }

    private double Occupied(UIElement child) => child.DesiredSize.Width > 0 ? child.DesiredSize.Width + Spacing : 0;
}
