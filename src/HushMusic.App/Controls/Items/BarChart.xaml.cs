using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;

namespace HushMusic.App.Controls.Items;

/// <summary>One bar of a <see cref="BarChart"/>.</summary>
/// <param name="Label">Shown under the bar; empty to leave it unlabelled.</param>
/// <param name="Ratio">Height relative to the tallest bar, 0 to 1.</param>
/// <param name="Description">Tooltip and screen reader text, e.g. "Monday: 2 h 10 min".</param>
public sealed record ChartBar(string Label, double Ratio, string Description);

/// <summary>A small, plain column chart (listening by hour, by weekday): accent bars on a hairline baseline.</summary>
public sealed partial class BarChart : UserControl
{
    public static readonly DependencyProperty BarsProperty = DependencyProperty.Register(
        nameof(Bars), typeof(IReadOnlyList<ChartBar>), typeof(BarChart), new PropertyMetadata(null, (d, _) => ((BarChart)d).Rebuild()));

    public static readonly DependencyProperty PlotHeightProperty = DependencyProperty.Register(
        nameof(PlotHeight), typeof(double), typeof(BarChart), new PropertyMetadata(120d, (d, _) => ((BarChart)d).Rebuild()));

    public BarChart()
    {
        InitializeComponent();
    }

    public IReadOnlyList<ChartBar>? Bars
    {
        get => (IReadOnlyList<ChartBar>?)GetValue(BarsProperty);
        set => SetValue(BarsProperty, value);
    }

    public double PlotHeight
    {
        get => (double)GetValue(PlotHeightProperty);
        set => SetValue(PlotHeightProperty, value);
    }

    private void Rebuild()
    {
        Columns.Children.Clear();
        Columns.ColumnDefinitions.Clear();
        Columns.Children.Add(Baseline);
        PlotRow.Height = new GridLength(PlotHeight);

        var bars = Bars ?? [];
        Grid.SetColumnSpan(Baseline, Math.Max(1, bars.Count));
        if (bars.Count == 0)
        {
            return;
        }

        var resources = Application.Current.Resources;
        var accent = (Brush)resources["AccentBrush"];
        var labelStyle = (Style)resources["FootnoteStyle"];

        // Rounded tops, a little tighter than the small radius so thin bars keep a flat-ish cap.
        var radius = ((CornerRadius)resources["RadiusSmall"]).TopLeft / 2;
        for (var i = 0; i < bars.Count; i++)
        {
            var bar = bars[i];
            Columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // The column (bar and the space under it) carries the tooltip and the screen reader text.
            var column = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            Grid.SetColumn(column, i);
            Grid.SetRowSpan(column, 2);
            AutomationProperties.SetName(column, bar.Description);
            ToolTipService.SetToolTip(column, bar.Description);

            var ratio = double.IsFinite(bar.Ratio) ? Math.Clamp(bar.Ratio, 0, 1) : 0;
            if (ratio > 0)
            {
                var fill = new Border
                {
                    Height = Math.Max(2, ratio * PlotHeight),
                    Margin = new Thickness(bars.Count > 12 ? 2 : 6, 0, bars.Count > 12 ? 2 : 6, 0),
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Background = accent,
                    CornerRadius = new CornerRadius(radius, radius, 0, 0),
                };
                Grid.SetColumn(fill, i);
                Columns.Children.Add(fill);
            }

            Columns.Children.Add(column);
            if (!string.IsNullOrEmpty(bar.Label))
            {
                Columns.Children.Add(CreateLabel(bars, i, labelStyle));
            }
        }
    }

    // A label followed by unlabelled bars (every sixth hour) starts at its bar and may run over the next ones; a bar
    // with its own label (weekdays) is labelled in its middle.
    private TextBlock CreateLabel(IReadOnlyList<ChartBar> bars, int index, Style style)
    {
        var span = 1;
        while (index + span < bars.Count && string.IsNullOrEmpty(bars[index + span].Label))
        {
            span++;
        }

        var label = new TextBlock
        {
            Style = style,
            Text = bars[index].Label,
            Margin = new Thickness(span > 1 ? 2 : 0, 6, 0, 0),
            HorizontalAlignment = span > 1 ? HorizontalAlignment.Left : HorizontalAlignment.Center,
            TextTrimming = TextTrimming.None,
            TextWrapping = TextWrapping.NoWrap,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAccessibilityView(label, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetRow(label, 1);
        Grid.SetColumn(label, index);
        Grid.SetColumnSpan(label, span);
        return label;
    }
}
