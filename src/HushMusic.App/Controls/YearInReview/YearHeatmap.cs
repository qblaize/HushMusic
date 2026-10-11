using System.Globalization;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.YearInReview;

namespace HushMusic.App.Controls.YearInReview;

/// <summary>
/// Every day of a year as a small square, a column per week (the week starts where the user's region starts it), January
/// to June above July to December: a faint grey for no music, then deeper shades of the accent for more listening. Days
/// still to come are fainter still.
/// </summary>
public sealed partial class YearHeatmap : Grid, IRecapEntrance
{
    public static readonly DependencyProperty SlideProperty = DependencyProperty.Register(
        nameof(Slide), typeof(RecapSlide), typeof(YearHeatmap), new PropertyMetadata(null, (d, _) => ((YearHeatmap)d).Build()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(YearHeatmap), new PropertyMetadata(null, (d, _) => ((YearHeatmap)d).Build()));

    private const double GridWidth = 408;
    private const double Gap = 3;
    private const double MonthLabelHeight = 18;
    private const double StripGap = 16;

    private static readonly TimeSpan ColumnStep = TimeSpan.FromMilliseconds(9);
    private static readonly TimeSpan ColumnDuration = TimeSpan.FromMilliseconds(350);

    private readonly Canvas _canvas = new();
    private readonly List<Canvas> _columns = [];

    public YearHeatmap()
    {
        Width = GridWidth;
        HorizontalAlignment = HorizontalAlignment.Left;
        Children.Add(_canvas);
    }

    /// <summary>The days to show: listening per day, the first day and the last one so far.</summary>
    public RecapSlide? Slide
    {
        get => (RecapSlide?)GetValue(SlideProperty);
        set => SetValue(SlideProperty, value);
    }

    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    // The weeks sweep in from January to December.
    public void Enter(TimeSpan delay)
    {
        for (var i = 0; i < _columns.Count; i++)
        {
            var visual = ElementCompositionPreview.GetElementVisual(_columns[i]);
            var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(1f, 1f);
            fade.Duration = ColumnDuration;
            fade.DelayTime = delay + (ColumnStep * i);
            fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation(nameof(Visual.Opacity), fade);
        }
    }

    public void Settle()
    {
        foreach (var column in _columns)
        {
            var visual = ElementCompositionPreview.GetElementVisual(column);
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 1;
        }
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _columns.Clear();
        if (Slide is not DaysSlide { Days.Count: > 0 } slide)
        {
            Height = 0;
            return;
        }

        // Two half years, one above the other: big enough squares to read on a card.
        var firstDay = slide.FirstDay;
        var july = new DateOnly(firstDay.Year, 7, 1);
        var halves = new[] { (First: firstDay, Last: july.AddDays(-1)), (First: july, Last: new DateOnly(firstDay.Year, 12, 31)) };
        var firstDayOfWeek = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var weeks = halves.Max(h => Lead(h.First, firstDayOfWeek) + h.Last.DayNumber - h.First.DayNumber + 7) / 7;
        var pitch = (GridWidth + Gap) / weeks;
        var cell = pitch - Gap;
        var stripHeight = MonthLabelHeight + (7 * pitch) - Gap;

        var empty = ThemeResources.Get<Brush>("TextPrimaryBrush", ElementTheme.Dark);
        var accent = Accent ?? empty;
        var labelStyle = (Style)Application.Current.Resources["FootnoteStyle"];
        var culture = CultureInfo.CurrentCulture;
        for (var half = 0; half < halves.Length; half++)
        {
            var (first, last) = halves[half];
            var top = half * (stripHeight + StripGap);
            var lead = Lead(first, firstDayOfWeek);
            Canvas? column = null;
            for (var day = first; day <= last; day = day.AddDays(1))
            {
                var slot = lead + (day.DayNumber - first.DayNumber);
                if (column is null || slot % 7 == 0)
                {
                    column = new Canvas { Width = cell, Height = stripHeight };
                    Canvas.SetLeft(column, slot / 7 * pitch);
                    Canvas.SetTop(column, top);
                    _canvas.Children.Add(column);
                    _columns.Add(column);
                }

                // Each month is named above the week it starts in.
                if (day.Day == 1)
                {
                    var label = new TextBlock { Style = labelStyle, FontSize = 11, Text = day.ToString("MMM", culture) };
                    Canvas.SetTop(label, -2);
                    column.Children.Add(label);
                }

                var index = day.DayNumber - firstDay.DayNumber;
                var ratio = index < slide.Days.Count ? Math.Clamp(slide.Days[index], 0, 1) : 0;
                var square = new Rectangle
                {
                    Width = cell,
                    Height = cell,
                    RadiusX = 2,
                    RadiusY = 2,
                    Fill = ratio > 0 ? accent : empty,
                    Opacity = day > slide.LastDay ? 0.04 : Shade(ratio),
                };
                Canvas.SetTop(square, MonthLabelHeight + (slot % 7 * pitch));
                column.Children.Add(square);
            }
        }

        Height = (2 * stripHeight) + StripGap;
        _canvas.Height = Height;
    }

    // Empty squares before the first day, so each column is one week from the region's first day of the week.
    private static int Lead(DateOnly first, DayOfWeek firstDayOfWeek) => ((int)first.DayOfWeek - (int)firstDayOfWeek + 7) % 7;

    // Four steps of the accent, so a quiet day still shows up; days without music are a faint grey.
    private static double Shade(double ratio) => ratio switch
    {
        <= 0 => 0.1,
        < 0.25 => 0.35,
        < 0.5 => 0.55,
        < 0.75 => 0.78,
        _ => 1,
    };
}
