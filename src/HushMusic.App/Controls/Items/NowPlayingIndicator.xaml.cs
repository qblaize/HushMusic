using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Shapes;
using HushMusic.App.Helpers;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Three small equaliser bars marking the playing track: they bounce while playing and freeze when paused. They also
/// freeze while they can't be seen (a collapsed view, the window minimized or hidden; see <see cref="AnimationGate"/>).
/// </summary>
/// <remarks>
/// While any animation runs, the compositor redraws the window on every frame, whatever its size or how often its
/// value changes. That is cheap on ordinary pages but costs several percent of a core over the full-window Now Playing
/// view, which therefore shows the bars still (<see cref="IsAnimated"/> = false).
/// </remarks>
public sealed partial class NowPlayingIndicator : UserControl
{
    public static readonly DependencyProperty IsPlayingProperty = DependencyProperty.Register(
        nameof(IsPlaying), typeof(bool), typeof(NowPlayingIndicator), new PropertyMetadata(false, (d, _) => ((NowPlayingIndicator)d).Update()));

    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated), typeof(bool), typeof(NowPlayingIndicator), new PropertyMetadata(true, (d, _) => ((NowPlayingIndicator)d).Update()));

    private const int SamplesPerSecond = 12;
    private const string ScaleY = "Scale.Y";

    private readonly AnimationGate _gate;
    private bool _started;

    public NowPlayingIndicator()
    {
        InitializeComponent();

        // Resting pose until the first bounce (and while nothing plays).
        Pose(Bar1, 0.55f);
        Pose(Bar2, 0.9f);
        Pose(Bar3, 0.65f);
        _gate = new AnimationGate(this, SetBouncing);
    }

    public bool IsPlaying
    {
        get => (bool)GetValue(IsPlayingProperty);
        set => SetValue(IsPlayingProperty, value);
    }

    /// <summary>
    /// False keeps the bars still (a static "playing" mark). Used in Now Playing, where any running animation makes the
    /// compositor redraw the whole full-window view on every frame.
    /// </summary>
    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    /// <summary>Call after changing <see cref="UIElement.Visibility"/>: hidden indicators don't animate.</summary>
    public void Refresh() => Update();

    private void OnLoaded(object sender, RoutedEventArgs e) => Update();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        foreach (var bar in Bars())
        {
            ElementCompositionPreview.GetElementVisual(bar).StopAnimation(ScaleY);
        }

        _started = false;
    }

    private void Update() => _gate.IsWanted = IsAnimated && IsPlaying && Visibility == Visibility.Visible;

    private IEnumerable<Rectangle> Bars() => [Bar1, Bar2, Bar3];

    // Paused in place, so the bars hold their pose instead of jumping back.
    private void SetBouncing(bool bounce)
    {
        if (!_started)
        {
            if (!bounce)
            {
                return;
            }

            Start(Bar1, 0.42, 0.3f, 1f);
            Start(Bar2, 0.55, 0.85f, 0.25f);
            Start(Bar3, 0.36, 0.45f, 0.95f);
            _started = true;
            return;
        }

        foreach (var bar in Bars())
        {
            var controller = ElementCompositionPreview.GetElementVisual(bar).TryGetAnimationController(ScaleY);
            if (bounce)
            {
                controller?.Resume();
            }
            else
            {
                controller?.Pause();
            }
        }
    }

    private static void Pose(Rectangle bar, float scaleY)
    {
        var visual = ElementCompositionPreview.GetElementVisual(bar);
        visual.CenterPoint = new Vector3((float)bar.Width / 2, (float)bar.Height, 0);
        visual.Scale = new Vector3(1, scaleY, 1);
    }

    // One bar swinging between two heights (eased like a sine) on its own period, so the three never line up.
    private static void Start(Rectangle bar, double halfPeriod, float from, float to)
    {
        var visual = ElementCompositionPreview.GetElementVisual(bar);
        var compositor = visual.Compositor;
        var period = TimeSpan.FromSeconds(2 * halfPeriod);
        var steps = Math.Max(2, (int)Math.Round(period.TotalSeconds * SamplesPerSecond));
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = period;
        animation.IterationBehavior = AnimationIterationBehavior.Forever;
        for (var i = 0; i <= steps; i++)
        {
            var progress = (float)i / steps;
            var x = progress <= 0.5f ? progress * 2 : 2 - (progress * 2);
            var value = from + ((to - from) * (0.5f - (0.5f * MathF.Cos(MathF.PI * x))));
            animation.InsertKeyFrame(progress, value);
        }

        visual.StartAnimation(ScaleY, animation);
    }
}
