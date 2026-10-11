using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.YearInReview;

namespace HushMusic.App.Controls.YearInReview;

/// <summary>
/// Listening by hour of the day as a 24-hour clock: one bar per hour pointing outwards from the centre, longer for more
/// listening, the busiest one in the accent colour. Midnight is at the top. The middle is left free for a label.
/// </summary>
public sealed partial class ListeningClock : Grid, IRecapEntrance
{
    public static readonly DependencyProperty SlideProperty = DependencyProperty.Register(
        nameof(Slide), typeof(RecapSlide), typeof(ListeningClock), new PropertyMetadata(null, (d, _) => ((ListeningClock)d).Build()));

    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(ListeningClock), new PropertyMetadata(null, (d, _) => ((ListeningClock)d).Build()));

    private const double ClockWidth = 340;
    private const double ClockHeight = 284;
    private const double InnerRadius = 58;
    private const double MaxBarLength = 60;
    private const double MinBarLength = 4;
    private const double BarWidth = 8;
    private const double LabelGap = 6;

    private static readonly TimeSpan BarStep = TimeSpan.FromMilliseconds(22);
    private static readonly TimeSpan BarDuration = TimeSpan.FromMilliseconds(650);

    private readonly Canvas _canvas = new() { Width = ClockWidth, Height = ClockHeight };
    private readonly List<(Rectangle Bar, double Length)> _bars = [];

    public ListeningClock()
    {
        Width = ClockWidth;
        Height = ClockHeight;
        Children.Add(_canvas);
    }

    /// <summary>The hours to show (listening per hour, relative to the busiest) and the busiest one.</summary>
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

    public void Enter(TimeSpan delay)
    {
        foreach (var (bar, length) in _bars)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            var compositor = visual.Compositor;

            // Each bar grows outwards from the ring, one hour after the other around the clock.
            visual.CenterPoint = new Vector3((float)(BarWidth / 2), (float)length, 0);
            var grow = compositor.CreateVector3KeyFrameAnimation();
            grow.InsertKeyFrame(0f, new Vector3(1, 0, 1));
            grow.InsertKeyFrame(1f, Vector3.One, RecapMotion.Ease(compositor));
            grow.Duration = BarDuration;
            grow.DelayTime = delay + (BarStep * _bars.FindIndex(b => ReferenceEquals(b.Bar, bar)));
            grow.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
            visual.StartAnimation(nameof(Visual.Scale), grow);
        }
    }

    public void Settle()
    {
        foreach (var (bar, _) in _bars)
        {
            var visual = ElementCompositionPreview.GetElementVisual(bar);
            visual.StopAnimation(nameof(Visual.Scale));
            visual.Scale = Vector3.One;
        }
    }

    private void Build()
    {
        _canvas.Children.Clear();
        _bars.Clear();
        if (Slide is not ClockSlide { Hours.Count: 24 } slide)
        {
            return;
        }

        var hours = slide.Hours;

        // The recap is always dark.
        var dim = ThemeResources.Get<Brush>("TextPrimaryBrush", ElementTheme.Dark);
        var accent = Accent ?? dim;
        var labelStyle = (Style)Application.Current.Resources["FootnoteStyle"];
        var centerX = ClockWidth / 2;
        var centerY = ClockHeight / 2;
        var outer = InnerRadius + MaxBarLength;

        for (var hour = 0; hour < 24; hour++)
        {
            var length = MinBarLength + (Math.Clamp(hours[hour], 0, 1) * (MaxBarLength - MinBarLength));
            var isPeak = hour == slide.PeakHour;

            // An arm from the centre to the outer edge, turned to its hour; the bar sits on its outer part.
            var arm = new Canvas
            {
                Width = BarWidth,
                Height = outer,
                RenderTransform = new RotateTransform { Angle = hour * 15, CenterX = BarWidth / 2, CenterY = outer },
            };
            Canvas.SetLeft(arm, centerX - (BarWidth / 2));
            Canvas.SetTop(arm, centerY - outer);

            var bar = new Rectangle
            {
                Width = BarWidth,
                Height = length,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Fill = isPeak ? accent : dim,
                Opacity = isPeak ? 1 : 0.3,
            };
            Canvas.SetTop(bar, outer - InnerRadius - length);
            arm.Children.Add(bar);
            _canvas.Children.Add(arm);
            _bars.Add((bar, length));
        }

        // Quarter marks just outside the bars: midnight at the top, then clockwise.
        var culture = CultureInfo.CurrentCulture;
        var reach = outer + LabelGap;
        foreach (var hour in (ReadOnlySpan<int>)[0, 6, 12, 18])
        {
            var label = new TextBlock { Style = labelStyle, Text = RecapSlideBuilder.HourLabel(hour, culture), Opacity = 0.7 };
            label.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var (width, height) = (label.DesiredSize.Width, label.DesiredSize.Height);
            var (left, top) = hour switch
            {
                0 => (centerX - (width / 2), centerY - reach - height),
                6 => (centerX + reach, centerY - (height / 2)),
                12 => (centerX - (width / 2), centerY + reach),
                _ => (centerX - reach - width, centerY - (height / 2)),
            };
            Canvas.SetLeft(label, left);
            Canvas.SetTop(label, top);
            _canvas.Children.Add(label);
        }
    }
}
