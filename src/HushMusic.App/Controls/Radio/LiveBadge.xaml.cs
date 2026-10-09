using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;

namespace HushMusic.App.Controls.Radio;

/// <summary>
/// The "• LIVE" eyebrow shown above the title while a radio station plays: a small pill in the accent tint with accent
/// text, its dot gently pulsing (static when Windows animations are off). Both come from the theme-aware accent brushes,
/// so the badge reads on the light app and inside dark surfaces (Now Playing, mini player) alike.
/// The dot pulses only while the station plays and the badge is on screen (<see cref="AnimationGate"/>).
/// </summary>
public sealed partial class LiveBadge : UserControl
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(LiveBadge), new PropertyMetadata(null, (d, _) => ((LiveBadge)d).UpdateBrushes()));

    private static readonly TimeSpan HalfPulse = TimeSpan.FromMilliseconds(800);
    private static bool? s_animationsEnabled;

    private readonly AnimationGate _gate;
    private readonly INowPlayingService? _nowPlaying = App.Services?.GetService<INowPlayingService>();
    private Storyboard? _pulse;
    private bool _pulsing;

    public LiveBadge()
    {
        InitializeComponent();
        _gate = new AnimationGate(this, SetPulsing);
        Loaded += (_, _) =>
        {
            if (_nowPlaying is not null)
            {
                _nowPlaying.Changed -= OnNowPlayingChanged;
                _nowPlaying.Changed += OnNowPlayingChanged;
            }

            UpdateWanted();
        };
        Unloaded += (_, _) =>
        {
            if (_nowPlaying is not null)
            {
                _nowPlaying.Changed -= OnNowPlayingChanged;
            }
        };
        RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => UpdateWanted());
    }

    /// <summary>Overrides the dot and text colour. Default: AccentTextBrush for the theme the badge shows.</summary>
    public Brush? Accent
    {
        get => (Brush?)GetValue(AccentProperty);
        set => SetValue(AccentProperty, value);
    }

    private static bool AnimationsEnabled => s_animationsEnabled ??= new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private void UpdateBrushes()
    {
        if (Accent is { } accent)
        {
            Dot.Fill = accent;
            Label.Foreground = accent;
        }
    }

    private void OnNowPlayingChanged(object? sender, EventArgs e) => UpdateWanted();

    private void UpdateWanted() =>
        _gate.IsWanted = Visibility == Visibility.Visible && AnimationsEnabled && _nowPlaying?.IsPlaying != false;

    // An explicit storyboard (implicit transitions crash when they fire during layout). Stopping it leaves the dot solid.
    private void SetPulsing(bool pulse)
    {
        if (pulse == _pulsing)
        {
            return;
        }

        _pulsing = pulse;
        if (!pulse)
        {
            _pulse?.Stop();
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
    }
}
