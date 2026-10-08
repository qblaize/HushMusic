using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;

namespace HushMusic.App.Controls.Radio;

/// <summary>
/// The "• LIVE" eyebrow shown above the title while a radio station plays: a small pill in the soft accent with accent
/// text, its dot gently pulsing (static when Windows animations are off). Surfaces that stay dark (Now Playing, mini
/// player) set <see cref="Accent"/> to AccentOnDarkBrush.
/// </summary>
public sealed partial class LiveBadge : UserControl
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(LiveBadge), new PropertyMetadata(null, (d, _) => ((LiveBadge)d).UpdateBrushes()));

    private static readonly TimeSpan HalfPulse = TimeSpan.FromMilliseconds(800);
    private static bool? s_animationsEnabled;

    private Storyboard? _pulse;
    private bool _pulsing;

    public LiveBadge()
    {
        InitializeComponent();
        UpdateBrushes();
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => StopPulse();
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdatePulse());
    }

    /// <summary>The dot and the text. Default: AccentBrush.</summary>
    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static bool AnimationsEnabled => s_animationsEnabled ??= new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private void UpdateBrushes()
    {
        var accent = Accent ?? Application.Current.Resources["AccentBrush"] as Brush;
        Dot.Fill = accent;
        Label.Foreground = accent;
        Pill.Background = Application.Current.Resources["AccentSoftBrush"] as Brush;
    }

    // An explicit storyboard (implicit transitions crash when they fire during layout), only while shown.
    private void UpdatePulse()
    {
        if (!IsLoaded || Visibility != Visibility.Visible || !AnimationsEnabled)
        {
            StopPulse();
            return;
        }

        if (_pulsing)
        {
            return;
        }

        if (_pulse is null)
        {
            var fade = new DoubleAnimation
            {
                From = 1,
                To = 0.35,
                Duration = new Duration(HalfPulse),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(fade, Dot);
            Storyboard.SetTargetProperty(fade, nameof(Opacity));
            _pulse = new Storyboard();
            _pulse.Children.Add(fade);
        }

        _pulse.Begin();
        _pulsing = true;
    }

    private void StopPulse()
    {
        if (_pulsing)
        {
            _pulse?.Stop();
            _pulsing = false;
        }
    }
}
