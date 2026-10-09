using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Animation;
using HushMusic.App.Helpers;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Loading placeholder host. Its content is made of rounded Surface blocks (<c>SkeletonBlockStyle</c>,
/// <see cref="SkeletonRows"/>, <see cref="SkeletonCards"/>) and the whole thing gently "breathes" (an opacity pulse)
/// while it is on screen (<see cref="AnimationGate"/>). Static when "Animation effects" is off in Windows settings.
/// </summary>
public sealed partial class Skeleton : ContentControl
{
    private const double RestingOpacity = 0.55;
    private static readonly TimeSpan HalfCycle = TimeSpan.FromMilliseconds(1100);

    private static bool? s_animationsEnabled;

    private readonly AnimationGate _gate;
    private Storyboard? _breathe;
    private bool _isRunning;

    public Skeleton()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(this, "Loading");
        _gate = new AnimationGate(this, SetBreathing);
        Loaded += (_, _) => Update();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Update());
    }

    internal static bool AnimationsEnabled => s_animationsEnabled ??= new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private void Update()
    {
        if (!AnimationsEnabled)
        {
            Opacity = 0.8;
            return;
        }

        _gate.IsWanted = Visibility == Visibility.Visible;
    }

    private void SetBreathing(bool breathe)
    {
        if (breathe == _isRunning)
        {
            return;
        }

        _isRunning = breathe;
        if (!breathe)
        {
            _breathe?.Stop();
            return;
        }

        if (_breathe is null)
        {
            var pulse = new DoubleAnimation
            {
                From = 1,
                To = RestingOpacity,
                Duration = new Duration(HalfCycle),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(pulse, this);
            Storyboard.SetTargetProperty(pulse, nameof(Opacity));
            _breathe = new Storyboard();
            _breathe.Children.Add(pulse);
        }

        _breathe.Begin();
    }
}
