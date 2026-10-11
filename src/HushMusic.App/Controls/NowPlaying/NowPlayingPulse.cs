using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Windows.UI.ViewManagement;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;
using HushMusic.App.Services.Shell;
using HushMusic.Playback.Pulse;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// "Pulse to the music" in Now Playing: the blurred backdrop swells and brightens on the beat, and a glow behind the
/// cover breathes with the bass. The levels come from <see cref="IMusicPulse"/>, which captures the app's audio only
/// while this listens: the view is open with the setting on (<see cref="SetEnabled"/>), music plays, the window is on
/// screen (<see cref="AnimationGate"/>) and Windows animations are on.
/// </summary>
/// <remarks>
/// Every change redraws the whole full-window view, frosted layers and all, on the UI thread: about 2–3 ms of CPU a
/// frame. Running Composition animations would do that at the display's frame rate (that measured worse than setting
/// values), so values are set directly, from the latest levels (the capture thread delivers 25 a second), and only
/// when they changed visibly: at most <see cref="FramesPerSecond"/> times a second, except that a new beat shows at
/// once. Quiet music draws nothing. The backdrop's drift steps along in the same frames (<see cref="ArtworkBackdrop"/>).
/// </remarks>
internal sealed class NowPlayingPulse
{
    private const int FramesPerSecond = 15;

    // How far the backdrop swells (fraction of its size) and how much white washes over it at full strength (less in the
    // Windows design, whose backdrop isn't frosted over); the glow's opacity and size from rest to full.
    private const float MaxSwell = 0.07f;
    private const float MaxLift = 0.2f;
    private const float WindowsDesignLift = 0.55f;
    private const float GlowRestOpacity = 0.1f;
    private const float GlowPeakOpacity = 0.9f;
    private const float GlowRestScale = 0.92f;
    private const float GlowGrowth = 0.2f;

    // Strength changes (0 – 1) smaller than this aren't drawn, and below Rest a strength is drawn as none.
    private const float Threshold = 0.03f;
    private const float Rest = 0.015f;

    // The beat level jumping up by this much is a new beat.
    private const float BeatJump = 0.15f;

    // Levels closer than this to the last ones handed to the UI thread aren't handed over.
    private const float PostThreshold = 0.005f;

    private static readonly long FrameTicks = Stopwatch.Frequency / FramesPerSecond;

    private readonly ArtworkBackdrop _backdrop;
    private readonly PulseGlow _glow;
    private readonly IMusicPulse _pulse;
    private readonly float _maxLift = DesignSystems.IsWindowsActive ? MaxLift * WindowsDesignLift : MaxLift;
    private readonly INowPlayingService? _nowPlaying = App.Services?.GetService<INowPlayingService>();
    private readonly UISettings _uiSettings = new();
    private readonly AnimationGate _gate;
    private readonly DispatcherQueue _dispatcher;
    private readonly object _latestLock = new();
    private MusicPulseLevels _latest;
    private MusicPulseLevels _posted;
    private int _applyQueued;
    private IDisposable? _listening;
    private bool _enabled;

    // What shows (strengths 0 – 1), since when, and the beat level last seen.
    private bool _shown;
    private float _shownBackdrop;
    private float _shownGlow;
    private long _shownAt;
    private float _lastBeat;

    public NowPlayingPulse(FrameworkElement host, ArtworkBackdrop backdrop, PulseGlow glow, IMusicPulse pulse)
    {
        _backdrop = backdrop;
        _glow = glow;
        _pulse = pulse;
        _dispatcher = host.DispatcherQueue;
        _gate = new AnimationGate(host, OnGateChanged);
        host.Loaded += (_, _) =>
        {
            if (_nowPlaying is not null)
            {
                _nowPlaying.Changed -= OnNowPlayingChanged;
                _nowPlaying.Changed += OnNowPlayingChanged;
            }

            UpdateWanted();
        };
        host.Unloaded += (_, _) =>
        {
            if (_nowPlaying is not null)
            {
                _nowPlaying.Changed -= OnNowPlayingChanged;
            }
        };
    }

    /// <summary>
    /// The view is open and wants the effect (or not: closing, setting off, Minimal layout). Windows' "Animation effects"
    /// off turns it off too.
    /// </summary>
    public void SetEnabled(bool enabled)
    {
        _enabled = enabled && _uiSettings.AnimationsEnabled;
        UpdateWanted();
    }

    private void OnNowPlayingChanged(object? sender, EventArgs e) => UpdateWanted();

    private void UpdateWanted() => _gate.IsWanted = _enabled && _nowPlaying?.IsPlaying != false;

    private void OnGateChanged(bool open)
    {
        if (open)
        {
            _shown = false;
            _lastBeat = 0;
            _posted = new MusicPulseLevels(float.NaN, float.NaN, float.NaN);
            _listening ??= _pulse.Listen(OnLevels);
            return;
        }

        _listening?.Dispose();
        _listening = null;

        // Ease back to the still view; at once when nobody sees it or animations are off.
        var animate = _uiSettings.AnimationsEnabled && WindowPresence.IsShown;
        _backdrop.RestPulse(animate);
        _glow.Rest(animate);
    }

    // Capture thread: keep the latest levels and make sure one apply is queued (none while the levels hold still, as in
    // silence).
    private void OnLevels(MusicPulseLevels levels)
    {
        if (Math.Abs(levels.Bass - _posted.Bass) < PostThreshold && Math.Abs(levels.Beat - _posted.Beat) < PostThreshold)
        {
            return;
        }

        _posted = levels;
        lock (_latestLock)
        {
            _latest = levels;
        }

        if (Interlocked.Exchange(ref _applyQueued, 1) == 0 && !_dispatcher.TryEnqueue(Apply))
        {
            Volatile.Write(ref _applyQueued, 0);
        }
    }

    private void Apply()
    {
        Volatile.Write(ref _applyQueued, 0);
        if (_listening is null)
        {
            return;
        }

        MusicPulseLevels levels;
        lock (_latestLock)
        {
            levels = _latest;
        }

        var bass = Math.Clamp(levels.Bass, 0, 1);
        var beat = Math.Clamp(levels.Beat, 0, 1);
        var onset = beat - _lastBeat > BeatJump;
        _lastBeat = beat;

        // The backdrop follows the beat more, the glow the bass.
        var backdrop = Strength(beat + (0.3f * bass));
        var glow = Strength((0.8f * bass) + (0.5f * beat));
        var now = Stopwatch.GetTimestamp();
        if (_shown)
        {
            var changed = Math.Abs(backdrop - _shownBackdrop) >= Threshold || Math.Abs(glow - _shownGlow) >= Threshold
                || (backdrop == 0 && _shownBackdrop != 0) || (glow == 0 && _shownGlow != 0);
            if (!changed || (!onset && now - _shownAt < FrameTicks))
            {
                return;
            }
        }

        _shown = true;
        _shownBackdrop = backdrop;
        _shownGlow = glow;
        _shownAt = now;
        _backdrop.SetPulse(MaxSwell * backdrop, _maxLift * backdrop);
        _glow.Set(GlowRestOpacity + ((GlowPeakOpacity - GlowRestOpacity) * glow), GlowRestScale + (GlowGrowth * glow));

        static float Strength(float value) => value < Rest ? 0 : Math.Min(value, 1);
    }
}
