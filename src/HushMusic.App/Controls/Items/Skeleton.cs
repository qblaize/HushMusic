using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media.Animation;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Loading placeholder host. Its content is made of rounded Surface blocks (<c>SkeletonBlockStyle</c>,
/// <see cref="SkeletonRows"/>, <see cref="SkeletonCards"/>) and the whole thing gently "breathes" (an opacity pulse)
/// while it is loaded and visible. Static when "Animation effects" is off in Windows settings.
/// </summary>
public sealed partial class Skeleton : ContentControl
{
    private const double RestingOpacity = 0.55;
    private static readonly TimeSpan HalfCycle = TimeSpan.FromMilliseconds(1100);

    private static bool? s_animationsEnabled;

    private Storyboard? _breathe;
    private bool _isRunning;

    public Skeleton()
    {
        IsTabStop = false;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Top;
        AutomationProperties.SetName(this, "Loading");
        Loaded += (_, _) => Update();
        Unloaded += (_, _) => Stop();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => Update());
    }

    internal static bool AnimationsEnabled => s_animationsEnabled ??= new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private void Update()
    {
        if (IsLoaded && Visibility == Visibility.Visible)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    private void Start()
    {
        if (_isRunning)
        {
            return;
        }

        if (!AnimationsEnabled)
        {
            Opacity = 0.8;
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
        _isRunning = true;
    }

    private void Stop()
    {
        if (_isRunning)
        {
            _breathe?.Stop();
            _isRunning = false;
        }
    }
}
