using System.Diagnostics;
using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The Now Playing background: the current album art decoded at a few pixels and stretched over the window (bilinear
/// upscaling is the blur; no Win2D), slowly drifting and cross-fading on track changes. The view adds the dark,
/// frosted scrim on top.
/// </summary>
/// <remarks>
/// Every frame of the drift redraws the whole window, and the frosted layers above it blur it again: as a 60 fps
/// Composition animation it took about half a CPU core. It moves a few pixels a second under a heavy blur, so it is
/// stepped instead, <see cref="DriftFramesPerSecond"/> times a second: each step is a small fraction of one pixel of the
/// stretched artwork, which can't be told apart from smooth motion. It runs only while music plays, the window is on
/// screen and the "Moving background" setting is on; otherwise it holds still where it is (<see cref="AnimationGate"/>).
/// </remarks>
public sealed partial class ArtworkBackdrop : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(ArtworkBackdrop), new PropertyMetadata(null, (d, e) => ((ArtworkBackdrop)d)._image.Url = e.NewValue as string));

    private const float BaseScale = 1.3f;
    private const float PeakScale = 1.48f;

    // The drift stays well inside the 15 % the base scale leaves at every edge.
    private const float DriftPixels = 40f;

    private const double DriftFramesPerSecond = 6;

    private static readonly TimeSpan DriftPeriod = TimeSpan.FromSeconds(40);

    // The drift's path: scale out and back over the period, and a loop through these offsets (at 0, ¼, ½, ¾ and 1),
    // each leg eased in and out.
    private static readonly Vector3[] DriftPath =
    [
        Vector3.Zero,
        new(-DriftPixels, DriftPixels * 0.6f, 0),
        new(DriftPixels * 0.5f, DriftPixels, 0),
        new(DriftPixels, -DriftPixels * 0.5f, 0),
        Vector3.Zero,
    ];

    private static readonly CubicBezier DriftEase = new(0.45, 0, 0.55, 1);

    private readonly CrossfadeImage _image = new()
    {
        DecodePixelWidth = 28,
        FadeDuration = TimeSpan.FromMilliseconds(900),
    };

    private readonly UISettings _uiSettings = new();
    private readonly AnimationGate _gate;
    private readonly INowPlayingService? _nowPlaying = App.Services?.GetService<INowPlayingService>();
    private readonly Stopwatch _driftClock = new();
    private Visual? _visual;
    private DispatcherQueueTimer? _driftTimer;
    private bool _drifting;

    public ArtworkBackdrop()
    {
        IsHitTestVisible = false;
        Children.Add(_image);
        _gate = new AnimationGate(this, OnGateChanged);
        SizeChanged += (_, e) =>
        {
            if (_visual is not null)
            {
                _visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            }
        };
        Loaded += (_, _) =>
        {
            EnsureVisual();
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
    }

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>False swaps art instantly (while the view is hidden).</summary>
    public bool Animate
    {
        get => _image.Animate;
        set => _image.Animate = value;
    }

    /// <summary>Reloads the art without a fade (after being hidden).</summary>
    public void Refresh() => _image.Refresh();

    /// <summary>Lets go of the decoded art (the view is closed); <see cref="Refresh"/> loads it again.</summary>
    public void Release() => _image.Release();

    /// <summary>
    /// Starts or stops the slow drift. Only runs while the view is open, and not when Windows animations are off; while
    /// it is on, it pauses when the music does or the window is hidden.
    /// </summary>
    public void SetDrifting(bool drifting)
    {
        var visual = EnsureVisual();
        if (visual is null)
        {
            return;
        }

        drifting &= _uiSettings.AnimationsEnabled;
        if (drifting == _drifting)
        {
            return;
        }

        _drifting = drifting;
        if (!drifting)
        {
            StopDrift(visual);
        }

        UpdateWanted();
    }

    private void OnNowPlayingChanged(object? sender, EventArgs e) => UpdateWanted();

    private void UpdateWanted() => _gate.IsWanted = _drifting && _nowPlaying?.IsPlaying != false;

    // Paused, it holds where it is, and continues from the same pose.
    private void OnGateChanged(bool open)
    {
        if (!open)
        {
            _driftTimer?.Stop();
            _driftClock.Stop();
            return;
        }

        if (_driftTimer is null)
        {
            _driftTimer = DispatcherQueue.CreateTimer();
            _driftTimer.Interval = TimeSpan.FromSeconds(1 / DriftFramesPerSecond);
            _driftTimer.Tick += (_, _) => ApplyDrift();
        }

        _driftClock.Start();
        _driftTimer.Start();
        ApplyDrift();
    }

    private void ApplyDrift()
    {
        if (_visual is not { } visual)
        {
            return;
        }

        var phase = (float)(_driftClock.Elapsed.TotalSeconds % DriftPeriod.TotalSeconds / DriftPeriod.TotalSeconds);
        var scale = phase < 0.5f
            ? Lerp(BaseScale, PeakScale, DriftEase.Ease(phase * 2))
            : Lerp(PeakScale, BaseScale, DriftEase.Ease((phase * 2) - 1));
        var leg = Math.Min((int)(phase * 4), 3);
        var offset = Vector3.Lerp(DriftPath[leg], DriftPath[leg + 1], DriftEase.Ease((phase * 4) - leg));

        visual.Scale = new Vector3(scale, scale, 1);
        visual.Properties.InsertVector3("Translation", offset);

        static float Lerp(float from, float to, float amount) => from + ((to - from) * amount);
    }

    private void StopDrift(Visual visual)
    {
        _driftTimer?.Stop();
        _driftClock.Reset();
        visual.Scale = new Vector3(BaseScale, BaseScale, 1);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
    }

    private Visual? EnsureVisual()
    {
        if (_visual is null && IsLoaded)
        {
            // The scaled, drifting image is larger than the backdrop: keep it from painting over the rail beside it.
            var host = ElementCompositionPreview.GetElementVisual(this);
            host.Clip = host.Compositor.CreateInsetClip();

            ElementCompositionPreview.SetIsTranslationEnabled(_image, true);
            _visual = ElementCompositionPreview.GetElementVisual(_image);
            _visual.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
            _visual.Scale = new Vector3(BaseScale, BaseScale, 1);
        }

        return _visual;
    }
}

/// <summary>A CSS-style cubic Bézier easing curve from (0, 0) to (1, 1) through two control points.</summary>
internal readonly record struct CubicBezier(double X1, double Y1, double X2, double Y2)
{
    /// <summary>The eased value at <paramref name="progress"/> (0–1).</summary>
    public float Ease(float progress)
    {
        var x = Math.Clamp((double)progress, 0, 1);

        // Solve x(t) = progress for t (Newton's method, with bisection as the fallback), then return y(t).
        var t = x;
        for (var i = 0; i < 8; i++)
        {
            var error = Sample(X1, X2, t) - x;
            var slope = Slope(X1, X2, t);
            if (Math.Abs(error) < 1e-6)
            {
                return (float)Sample(Y1, Y2, t);
            }

            if (Math.Abs(slope) < 1e-6)
            {
                break;
            }

            t = Math.Clamp(t - (error / slope), 0, 1);
        }

        double low = 0, high = 1;
        t = x;
        for (var i = 0; i < 30 && high - low > 1e-6; i++)
        {
            if (Sample(X1, X2, t) < x)
            {
                low = t;
            }
            else
            {
                high = t;
            }

            t = (low + high) / 2;
        }

        return (float)Sample(Y1, Y2, t);
    }

    private static double Sample(double a, double b, double t) => (3 * a * (1 - t) * (1 - t) * t) + (3 * b * (1 - t) * t * t) + (t * t * t);

    private static double Slope(double a, double b, double t) => (3 * a * (1 - t) * (1 - t)) + (6 * (b - a) * (1 - t) * t) + (3 * (1 - b) * t * t);
}
