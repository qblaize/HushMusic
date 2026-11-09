using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;

namespace HushMusic.App.Controls.Radio;

/// <summary>
/// The "• LIVE" eyebrow shown above the title while a radio station plays: a small pill in the accent tint with accent
/// text, its dot gently pulsing (static when Windows animations are off). Both come from the theme-aware accent brushes,
/// so the badge reads on the light app and inside dark surfaces (Now Playing, mini player) alike.
/// The dot pulses a few times when the station starts (or the badge comes back on screen), then stays solid: an endless
/// pulse would redraw the whole window every frame for as long as the station plays. It pulses only while the station
/// plays and the badge can be seen (<see cref="AnimationGate"/>). While the station connects again after losing its
/// stream, the badge reads "RECONNECTING…" and the dot keeps pulsing until it is back.
/// </summary>
public sealed partial class LiveBadge : UserControl
{
    public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
        nameof(Accent), typeof(Brush), typeof(LiveBadge), new PropertyMetadata(null, (d, _) => ((LiveBadge)d).UpdateBrushes()));

    private const int PulsesWhenPlaying = 3;
    private static readonly TimeSpan HalfPulse = TimeSpan.FromMilliseconds(800);
    private static bool? s_animationsEnabled;

    private readonly AnimationGate _gate;
    private readonly INowPlayingService? _nowPlaying = App.Services?.GetService<INowPlayingService>();
    private Storyboard? _pulse;
    private DoubleAnimation? _fade;
    private bool _pulsing;
    private bool _pulseForever;

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

    private void UpdateWanted()
    {
        _gate.IsWanted = Visibility == Visibility.Visible && AnimationsEnabled && _nowPlaying?.IsPlaying != false;

        // While the station connects again after losing its stream, the badge says so instead of a bare spinner.
        var reconnecting = _nowPlaying?.IsReconnecting == true;
        var text = reconnecting ? "RECONNECTING…" : "LIVE";
        if (Label.Text != text)
        {
            Label.Text = text;
            AutomationProperties.SetName(Label, reconnecting ? "Reconnecting" : "Live");
        }

        // Reconnecting pulses until the station is back; back on air, a few more pulses and then a solid dot.
        if (reconnecting != _pulseForever)
        {
            _pulseForever = reconnecting;
            if (_pulsing)
            {
                StartPulse();
            }
        }
    }

    // An explicit storyboard (implicit transitions crash when they fire during layout). Stopping it leaves the dot solid.
    private void SetPulsing(bool pulse)
    {
        if (pulse == _pulsing)
        {
            return;
        }

        _pulsing = pulse;
        if (pulse)
        {
            StartPulse();
        }
        else
        {
            _pulse?.Stop();
        }
    }

    // Starts over with the current repeat count. A finished pulse ends on its start value (AutoReverse): a solid dot.
    private void StartPulse()
    {
        if (_pulse is null || _fade is null)
        {
            _fade = new DoubleAnimation
            {
                From = 1,
                To = 0.35,
                Duration = new Duration(HalfPulse),
                AutoReverse = true,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(_fade, Dot);
            Storyboard.SetTargetProperty(_fade, nameof(Opacity));
            _pulse = new Storyboard();
            _pulse.Children.Add(_fade);
        }

        _pulse.Stop();
        _fade.RepeatBehavior = _pulseForever ? RepeatBehavior.Forever : new RepeatBehavior(PulsesWhenPlaying);
        _pulse.Begin();
    }
}
