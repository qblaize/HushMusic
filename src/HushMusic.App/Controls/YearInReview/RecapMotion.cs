using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace HushMusic.App.Controls.YearInReview;

public enum RecapEffect
{
    /// <summary>Fades in while rising a little.</summary>
    Rise,

    /// <summary>Fades in while growing to full size (covers, pictures).</summary>
    Zoom,

    /// <summary>Only fades in.</summary>
    Fade,
}

/// <summary>A part of a slide with an entrance of its own (the clock's bars, the year's days).</summary>
internal interface IRecapEntrance
{
    void Enter(TimeSpan delay);

    /// <summary>Ends the entrance at once, in its final pose.</summary>
    void Settle();
}

/// <summary>
/// The entrance of a recap slide: <c>yir:RecapMotion.Order="2"</c> on an element brings it in after the ones before it
/// (<see cref="Effect"/> says how), and <c>yir:RecapMotion.CountTo</c> on a TextBlock counts its number up from zero.
/// Composition animations, started once per slide; <see cref="Settle"/> jumps to the end (before an image is saved).
/// </summary>
public static class RecapMotion
{
    public static readonly DependencyProperty OrderProperty = DependencyProperty.RegisterAttached(
        "Order", typeof(int), typeof(RecapMotion), new PropertyMetadata(-1));

    public static readonly DependencyProperty EffectProperty = DependencyProperty.RegisterAttached(
        "Effect", typeof(RecapEffect), typeof(RecapMotion), new PropertyMetadata(RecapEffect.Rise));

    public static readonly DependencyProperty CountToProperty = DependencyProperty.RegisterAttached(
        "CountTo", typeof(long), typeof(RecapMotion), new PropertyMetadata(-1L, OnCountToChanged));

    /// <summary>The gap between one element's entrance and the next.</summary>
    internal static readonly TimeSpan Step = TimeSpan.FromMilliseconds(90);

    private const float RiseDistance = 26f;
    private const float ZoomFrom = 0.9f;

    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan MoveDuration = TimeSpan.FromMilliseconds(800);
    private static readonly TimeSpan CountDuration = TimeSpan.FromMilliseconds(1400);

    private static readonly List<Counter> s_counters = [];
    private static bool s_counting;

    public static int GetOrder(DependencyObject element) => (int)element.GetValue(OrderProperty);

    public static void SetOrder(DependencyObject element, int value) => element.SetValue(OrderProperty, value);

    public static RecapEffect GetEffect(DependencyObject element) => (RecapEffect)element.GetValue(EffectProperty);

    public static void SetEffect(DependencyObject element, RecapEffect value) => element.SetValue(EffectProperty, value);

    public static long GetCountTo(DependencyObject element) => (long)element.GetValue(CountToProperty);

    public static void SetCountTo(DependencyObject element, long value) => element.SetValue(CountToProperty, value);

    /// <summary>Plays the entrance of everything under <paramref name="root"/> (laid out already).</summary>
    internal static void Enter(UIElement root)
    {
        foreach (var element in Descendants(root))
        {
            var order = GetOrder(element);
            if (order < 0)
            {
                continue;
            }

            var delay = Step * order;
            if (element is IRecapEntrance entrance)
            {
                entrance.Enter(delay);
                continue;
            }

            Animate(element, GetEffect(element), delay);
            if (element is TextBlock text && GetCountTo(text) >= 0)
            {
                StartCount(text, delay);
            }
        }
    }

    /// <summary>Ends every entrance under <paramref name="root"/> at once.</summary>
    internal static void Settle(UIElement root)
    {
        foreach (var element in Descendants(root))
        {
            if (element is IRecapEntrance entrance)
            {
                entrance.Settle();
            }
            else if (GetOrder(element) >= 0)
            {
                var visual = ElementCompositionPreview.GetElementVisual(element);
                visual.StopAnimation(nameof(Visual.Opacity));
                visual.StopAnimation("Translation");
                visual.StopAnimation(nameof(Visual.Scale));
                visual.Opacity = 1;
                visual.Properties.InsertVector3("Translation", Vector3.Zero);
                visual.Scale = Vector3.One;
            }

            if (element is TextBlock text)
            {
                StopCount(text, finish: true);
            }
        }
    }

    /// <summary>Stops what is still counting under <paramref name="root"/> (it is going away).</summary>
    internal static void Stop(UIElement root)
    {
        foreach (var text in Descendants(root).OfType<TextBlock>())
        {
            StopCount(text, finish: false);
        }
    }

    /// <summary>The easing of every entrance: a long, soft deceleration.</summary>
    internal static CompositionEasingFunction Ease(Compositor compositor) =>
        compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f));

    private static void Animate(UIElement element, RecapEffect effect, TimeSpan delay)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var ease = Ease(compositor);

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = FadeDuration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        switch (effect)
        {
            case RecapEffect.Rise:
                var rise = compositor.CreateVector3KeyFrameAnimation();
                rise.InsertKeyFrame(0f, new Vector3(0, RiseDistance, 0));
                rise.InsertKeyFrame(1f, Vector3.Zero, ease);
                rise.Duration = MoveDuration;
                rise.DelayTime = delay;
                rise.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                visual.StartAnimation("Translation", rise);
                break;
            case RecapEffect.Zoom when element is FrameworkElement framed:
                visual.CenterPoint = new Vector3((float)framed.ActualWidth / 2, (float)framed.ActualHeight / 2, 0);
                var zoom = compositor.CreateVector3KeyFrameAnimation();
                zoom.InsertKeyFrame(0f, new Vector3(ZoomFrom, ZoomFrom, 1));
                zoom.InsertKeyFrame(1f, Vector3.One, ease);
                zoom.Duration = MoveDuration;
                zoom.DelayTime = delay;
                zoom.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;
                visual.StartAnimation(nameof(Visual.Scale), zoom);
                break;
        }
    }

    private static IEnumerable<UIElement> Descendants(UIElement root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(root, i) is not UIElement child)
            {
                continue;
            }

            yield return child;

            // An element with an entrance of its own animates its insides itself.
            if (child is not IRecapEntrance)
            {
                foreach (var nested in Descendants(child))
                {
                    yield return nested;
                }
            }
        }
    }

    // ===== Counting a number up, frame by frame, only while it counts =====

    private static void OnCountToChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBlock text && e.NewValue is long value && value >= 0)
        {
            text.Text = Formatted(value);
        }
    }

    private static string Formatted(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    private static void StartCount(TextBlock text, TimeSpan delay)
    {
        StopCount(text, finish: false);
        text.Text = Formatted(0);
        s_counters.Add(new Counter(text, GetCountTo(text), delay));
        if (!s_counting)
        {
            s_counting = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    private static void StopCount(TextBlock text, bool finish)
    {
        var index = s_counters.FindIndex(c => ReferenceEquals(c.Text, text));
        if (index < 0)
        {
            return;
        }

        if (finish)
        {
            text.Text = Formatted(s_counters[index].Target);
        }

        s_counters.RemoveAt(index);
        StopRenderingIfIdle();
    }

    private static void OnRendering(object? sender, object e)
    {
        for (var i = s_counters.Count - 1; i >= 0; i--)
        {
            var counter = s_counters[i];
            var t = Math.Clamp((counter.Clock.Elapsed - counter.Delay) / CountDuration, 0, 1);

            // Ease-out quint: races at first, then settles on the number.
            var eased = 1 - Math.Pow(1 - t, 5);
            counter.Text.Text = Formatted((long)Math.Round(counter.Target * eased));
            if (t >= 1)
            {
                s_counters.RemoveAt(i);
            }
        }

        StopRenderingIfIdle();
    }

    private static void StopRenderingIfIdle()
    {
        if (s_counting && s_counters.Count == 0)
        {
            s_counting = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }

    private sealed class Counter(TextBlock text, long target, TimeSpan delay)
    {
        public TextBlock Text { get; } = text;

        public long Target { get; } = target;

        public TimeSpan Delay { get; } = delay;

        public Stopwatch Clock { get; } = Stopwatch.StartNew();
    }
}
