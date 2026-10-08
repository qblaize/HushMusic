using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Windows.UI.ViewManagement;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The Now Playing background: the current album art decoded at a few pixels and stretched over the window (bilinear
/// upscaling is the blur; no Win2D), slowly drifting and cross-fading on track changes. The view adds the dark,
/// frosted scrim on top.
/// </summary>
public sealed partial class ArtworkBackdrop : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(ArtworkBackdrop), new PropertyMetadata(null, (d, e) => ((ArtworkBackdrop)d)._image.Url = e.NewValue as string));

    private const float BaseScale = 1.3f;
    private const float PeakScale = 1.48f;

    // The drift stays well inside the 15 % the base scale leaves at every edge.
    private const float DriftPixels = 40f;

    private static readonly TimeSpan DriftPeriod = TimeSpan.FromSeconds(40);

    private readonly CrossfadeImage _image = new()
    {
        DecodePixelWidth = 28,
        FadeDuration = TimeSpan.FromMilliseconds(900),
    };

    private readonly UISettings _uiSettings = new();
    private Visual? _visual;
    private bool _drifting;

    public ArtworkBackdrop()
    {
        IsHitTestVisible = false;
        Children.Add(_image);
        SizeChanged += (_, e) =>
        {
            if (_visual is not null)
            {
                _visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            }
        };
        Loaded += (_, _) => EnsureVisual();
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

    /// <summary>Starts or stops the slow drift. Only runs while the view is open, and not when Windows animations are off.</summary>
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
        visual.StopAnimation(nameof(Visual.Scale));
        visual.StopAnimation("Translation");
        visual.Scale = new Vector3(BaseScale, BaseScale, 1);
        visual.Properties.InsertVector3("Translation", Vector3.Zero);
        if (!drifting)
        {
            return;
        }

        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.45f, 0f), new Vector2(0.55f, 1f));

        var scale = compositor.CreateVector3KeyFrameAnimation();
        scale.Duration = DriftPeriod;
        scale.IterationBehavior = AnimationIterationBehavior.Forever;
        scale.InsertKeyFrame(0f, new Vector3(BaseScale, BaseScale, 1));
        scale.InsertKeyFrame(0.5f, new Vector3(PeakScale, PeakScale, 1), ease);
        scale.InsertKeyFrame(1f, new Vector3(BaseScale, BaseScale, 1), ease);

        var drift = compositor.CreateVector3KeyFrameAnimation();
        drift.Duration = DriftPeriod;
        drift.IterationBehavior = AnimationIterationBehavior.Forever;
        drift.InsertKeyFrame(0f, Vector3.Zero);
        drift.InsertKeyFrame(0.25f, new Vector3(-DriftPixels, DriftPixels * 0.6f, 0), ease);
        drift.InsertKeyFrame(0.5f, new Vector3(DriftPixels * 0.5f, DriftPixels, 0), ease);
        drift.InsertKeyFrame(0.75f, new Vector3(DriftPixels, -DriftPixels * 0.5f, 0), ease);
        drift.InsertKeyFrame(1f, Vector3.Zero, ease);

        visual.StartAnimation(nameof(Visual.Scale), scale);
        visual.StartAnimation("Translation", drift);
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
