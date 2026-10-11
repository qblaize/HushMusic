using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// A soft glow in the accent colour behind the Now Playing artwork for "Pulse to the music": <see cref="NowPlayingPulse"/>
/// makes it breathe with the bass; otherwise it is collapsed. Sized by the view to more than the cover, so a halo shows
/// around it.
/// </summary>
/// <remarks>
/// The glow is a tiny radial bitmap stretched over the element (bilinear upscaling keeps it smooth), as in AmbientGlow:
/// Composition gradient brushes drew hard bands on some GPUs. It is drawn again when the accent changes, following
/// <c>AccentOnDarkBrush</c> (Now Playing is always dark) while it shows.
/// </remarks>
public sealed partial class PulseGlow : Grid
{
    private const int GlowPixels = 64;

    // A rounded square like the cover: full strength inside Inner (behind the cover, which covers a little more than
    // half of the element), then fading to nothing at the element's edge, so the halo bleeds out evenly around all four
    // sides of the cover.
    private const double Inner = 0.5;
    private const double Roundness = 4;

    private static readonly TimeSpan RestDuration = TimeSpan.FromMilliseconds(450);

    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly byte[] _pixels = new byte[GlowPixels * GlowPixels * 4];
    private readonly SolidColorBrush? _accent;
    private WriteableBitmap? _bitmap;
    private Color _drawn;
    private long _accentToken;
    private Visual? _visual;
    private int _restVersion;

    public PulseGlow()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Children.Add(_image);
        _accent = FindAccent();
        SizeChanged += (_, e) =>
        {
            if (_visual is not null)
            {
                _visual.CenterPoint = new Vector3((float)e.NewSize.Width / 2, (float)e.NewSize.Height / 2, 0);
            }
        };
        Unloaded += (_, _) => FollowAccent(false);
    }

    /// <summary>
    /// Shows the glow at <paramref name="opacity"/> and <paramref name="scale"/> (of the element's size). The first call
    /// after <see cref="Rest"/> shows it.
    /// </summary>
    public void Set(float opacity, float scale)
    {
        if (Visibility != Visibility.Visible)
        {
            _restVersion++;
            FollowAccent(true);
            Visibility = Visibility.Visible;
        }

        var visual = EnsureVisual();
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation(nameof(Visual.Scale));
        visual.Opacity = opacity;
        visual.Scale = new Vector3(scale, scale, 1);
    }

    /// <summary>Fades the glow out (at once when <paramref name="animate"/> is false), then collapses it.</summary>
    public void Rest(bool animate)
    {
        if (Visibility != Visibility.Visible)
        {
            return;
        }

        var version = ++_restVersion;
        var visual = EnsureVisual();
        if (!animate)
        {
            Collapse();
            return;
        }

        var compositor = visual.Compositor;
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = RestDuration;
        fade.InsertKeyFrame(1f, 0f, easing);
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _restVersion)
            {
                Collapse();
            }
        });

        void Collapse()
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = 0;
            Visibility = Visibility.Collapsed;
            FollowAccent(false);
        }
    }

    // The indexer (unlike TryGetValue) also searches the merged dictionaries where Theme.xaml lives.
    private static SolidColorBrush? FindAccent()
    {
        try
        {
            return Application.Current.Resources["AccentOnDarkBrush"] as SolidColorBrush;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private Visual EnsureVisual()
    {
        if (_visual is null)
        {
            _visual = ElementCompositionPreview.GetElementVisual(this);
            _visual.CenterPoint = new Vector3((float)ActualWidth / 2, (float)ActualHeight / 2, 0);
            _visual.Opacity = 0;
        }

        return _visual;
    }

    // While it shows, the glow follows the accent (each step of the accent's cross-fade redraws the small bitmap).
    private void FollowAccent(bool follow)
    {
        if (_accent is null)
        {
            return;
        }

        if (follow && _accentToken == 0)
        {
            _accentToken = _accent.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => Draw());
            Draw();
        }
        else if (!follow && _accentToken != 0)
        {
            _accent.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, _accentToken);
            _accentToken = 0;
        }
    }

    // The accent, fading out from the cover's edge to the element's, premultiplied.
    private void Draw()
    {
        var color = _accent?.Color ?? default;
        if (_bitmap is not null && color.Equals(_drawn))
        {
            return;
        }

        _drawn = color;
        for (var y = 0; y < GlowPixels; y++)
        {
            for (var x = 0; x < GlowPixels; x++)
            {
                var dx = (((x + 0.5) / GlowPixels) * 2) - 1;
                var dy = (((y + 0.5) / GlowPixels) * 2) - 1;
                var distance = Math.Pow(Math.Pow(Math.Abs(dx), Roundness) + Math.Pow(Math.Abs(dy), Roundness), 1 / Roundness);
                var fade = Math.Clamp((distance - Inner) / (1 - Inner), 0, 1);
                var alpha = Math.Pow(1 - fade, 1.6);
                var i = ((y * GlowPixels) + x) * 4;
                _pixels[i] = (byte)Math.Round(color.B * alpha);
                _pixels[i + 1] = (byte)Math.Round(color.G * alpha);
                _pixels[i + 2] = (byte)Math.Round(color.R * alpha);
                _pixels[i + 3] = (byte)Math.Round(255 * alpha);
            }
        }

        if (_bitmap is null)
        {
            _bitmap = new WriteableBitmap(GlowPixels, GlowPixels);
            _image.Source = _bitmap;
        }

        using (var stream = _bitmap.PixelBuffer.AsStream())
        {
            stream.Write(_pixels, 0, _pixels.Length);
        }

        _bitmap.Invalidate();
    }
}
