using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.UI;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Controls.Shell;

/// <summary>
/// Ambient glow in the accent colour: a soft wash from the top-left behind the whole shell, blended over the background of
/// the theme shown. Two stretched bitmaps cross-fade on every accent change; a theme change re-renders instantly.
/// </summary>
/// <remarks>
/// The glow is a tiny bitmap computed here and stretched over the window (bilinear upscaling makes it smooth).
/// Composition gradient brushes are not used: on this hardware they drew hard white/black rings and bands
/// (radial and linear alike, with translucent or opaque stops).
/// </remarks>
public sealed partial class AmbientGlow : Grid
{
    private const int GlowPixels = 64;

    // A hint of colour, not a light show. On white the same amount reads much stronger, so light gets a subtler wash.
    private const double DarkStrength = 0.17;
    private const double LightStrength = 0.075;

    private static readonly Color DarkBackground = ColorHelper.FromArgb(0xFF, 0x0B, 0x0B, 0x0C);

    private readonly IAccentColorService _accent = App.GetService<IAccentColorService>();
    private readonly Image _glowA = new() { Stretch = Stretch.Fill };
    private readonly Image _glowB = new() { Stretch = Stretch.Fill, Opacity = 0 };

    private Color _background = DarkBackground;
    private double _strength = DarkStrength;
    private bool _showsA = true;

    public AmbientGlow()
    {
        IsHitTestVisible = false;
        Children.Add(_glowA);
        Children.Add(_glowB);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateTheme();
        _accent.Changed -= OnAccentChanged;
        _accent.Changed += OnAccentChanged;
        ActualThemeChanged -= OnActualThemeChanged;
        ActualThemeChanged += OnActualThemeChanged;
        Apply(_accent.Current, TimeSpan.Zero);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _accent.Changed -= OnAccentChanged;
        ActualThemeChanged -= OnActualThemeChanged;
    }

    private void OnAccentChanged(object? sender, AccentColorChangedEventArgs e) => Apply(e.Current, e.Transition);

    // The solid background behind the glow switches instantly, so the (opaque) glow does too.
    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        UpdateTheme();
        Apply(_accent.Current, TimeSpan.Zero);
    }

    // This element's own theme, not Application.Current.Resources (always the dark values).
    private void UpdateTheme()
    {
        var theme = ActualTheme;
        _background = ThemeResources.GetColor("AppBackgroundColor", theme, theme == ElementTheme.Light ? Colors.White : DarkBackground);
        _strength = theme == ElementTheme.Light ? LightStrength : DarkStrength;
    }

    // Cross-fades to a freshly rendered glow; the two images swap roles each time. Always through a storyboard (a zero
    // duration switches instantly): it takes over from a cross-fade still running, which local values would not.
    private void Apply(Color accent, TimeSpan transition)
    {
        var (incoming, outgoing) = _showsA ? (_glowB, _glowA) : (_glowA, _glowB);
        incoming.Source = Render(accent);
        _showsA = !_showsA;

        var duration = transition > TimeSpan.Zero ? transition : TimeSpan.Zero;
        var storyboard = new Storyboard();
        storyboard.Children.Add(Fade(incoming, 1, duration));
        storyboard.Children.Add(Fade(outgoing, 0, duration));
        storyboard.Begin();

        static DoubleAnimation Fade(UIElement target, double to, TimeSpan duration)
        {
            var animation = new DoubleAnimation { To = to, Duration = duration };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
            return animation;
        }
    }

    // The accent in the top-left corner, easing out to the plain background a bit past the middle.
    // Every pixel is opaque (pre-blended over the window background).
    private WriteableBitmap Render(Color accent)
    {
        var bitmap = new WriteableBitmap(GlowPixels, GlowPixels);
        var pixels = new byte[GlowPixels * GlowPixels * 4];
        for (var y = 0; y < GlowPixels; y++)
        {
            for (var x = 0; x < GlowPixels; x++)
            {
                var dx = ((x + 0.5) / GlowPixels) - 0.15;
                var dy = (y + 0.5) / GlowPixels;
                var distance = Math.Min(1, Math.Sqrt((dx * dx * 0.55) + (dy * dy)) / 0.85);
                var falloff = 1 - (distance * distance * (3 - (2 * distance)));
                var color = AccentPalette.Over(accent, _background, _strength * falloff);
                var i = ((y * GlowPixels) + x) * 4;
                pixels[i] = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = 0xFF;
            }
        }

        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}
