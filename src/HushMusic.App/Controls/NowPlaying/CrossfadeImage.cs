using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// Artwork that cross-fades when <see cref="Url"/> changes. Loads through the disk cache; when <see cref="Url"/> can't be
/// loaded it tries <see cref="FallbackUrl"/>. A small <see cref="DecodePixelWidth"/> decodes the art at a few pixels and
/// lets the GPU stretch it: a soft, blurred colour wash. Without one, the art is decoded at the size it is laid out at
/// (in steps, so resizing the window doesn't decode it again at every pixel), never at the source's full size.
/// </summary>
public sealed partial class CrossfadeImage : Grid
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(CrossfadeImage), new PropertyMetadata(null, (d, _) => ((CrossfadeImage)d).Reload()));

    public static readonly DependencyProperty FallbackUrlProperty = DependencyProperty.Register(
        nameof(FallbackUrl), typeof(string), typeof(CrossfadeImage), new PropertyMetadata(null));

    // A load that never reports back must not leave the old art up forever.
    private static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(4);

    // Laid-out sizes are rounded up to this many pixels before decoding.
    private const double DecodeStep = 64;

    private readonly Image _a = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private readonly Image _b = new() { Stretch = Stretch.UniformToFill, Opacity = 0 };
    private bool _showsA;
    private int _version;
    private Storyboard? _fade;
    private bool _released;
    private double _decodedWidth;

    public CrossfadeImage()
    {
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        Children.Add(_a);
        Children.Add(_b);
        SizeChanged += OnSizeChanged;
    }

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>Tried when <see cref="Url"/> fails (e.g. a resized URL the image host refuses).</summary>
    public string? FallbackUrl
    {
        get => (string?)GetValue(FallbackUrlProperty);
        set => SetValue(FallbackUrlProperty, value);
    }

    /// <summary>Physical pixels to decode at (0 = the laid-out size).</summary>
    public int DecodePixelWidth { get; set; }

    public TimeSpan FadeDuration { get; set; } = TimeSpan.FromMilliseconds(450);

    /// <summary>False swaps instantly (e.g. while hidden, where nothing would be seen anyway).</summary>
    public bool Animate { get; set; } = true;

    private static IImageCache? Cache => App.Services?.GetService<IImageCache>();

    private static ILogger? Logger => App.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(CrossfadeImage).FullName!);

    /// <summary>
    /// Loads <see cref="Url"/> again without a fade: after being hidden, where decoding may have been deferred, or after
    /// <see cref="Release"/>.
    /// </summary>
    public void Refresh()
    {
        _released = false;
        var animate = Animate;
        Animate = false;
        Reload();
        Animate = animate;
    }

    /// <summary>
    /// Lets go of the decoded art while it can't be seen (the view is closed). <see cref="Url"/> changes are only noted
    /// until <see cref="Refresh"/> loads the art again.
    /// </summary>
    public void Release()
    {
        _released = true;
        _version++;
        _fade?.Stop();
        _fade = null;
        foreach (var image in (Image[])[_a, _b])
        {
            image.Source = null;
            image.Opacity = 0;
        }

        _decodedWidth = 0;
    }

    private void Reload()
    {
        if (!_released)
        {
            _ = LoadAsync(Url, FallbackUrl, Animate);
        }
    }

    // A bigger slot than the art was decoded for (the window grew) loads it again, sharper.
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DecodePixelWidth == 0 && _decodedWidth > 0 && e.NewSize.Width > _decodedWidth && !_released)
        {
            Refresh();
        }
    }

    private async Task LoadAsync(string? url, string? fallback, bool animate)
    {
        var version = ++_version;
        var (incoming, outgoing) = _showsA ? (_b, _a) : (_a, _b);

        if (string.IsNullOrWhiteSpace(url))
        {
            Show(incoming: null, outgoing: _showsA ? _a : _b, version, animate);
            return;
        }

        ImageSource? source = null;
        foreach (var candidate in new[] { url, fallback }.Where(u => !string.IsNullOrWhiteSpace(u)).Distinct(StringComparer.Ordinal))
        {
            source = await OpenAsync(candidate!, version);
            if (source is not null || version != _version)
            {
                break;
            }
        }

        if (version != _version)
        {
            return;
        }

        incoming.Source = source;
        Show(source is null ? null : incoming, outgoing, version, animate);
    }

    // Resolves when the bitmap has decoded (or failed) so the cross-fade never shows a half-loaded image.
    private async Task<ImageSource?> OpenAsync(string url, int version)
    {
        try
        {
            var path = Cache is { } cache ? await cache.GetLocalPathAsync(url) : null;
            if (version != _version)
            {
                return null;
            }

            var uri = path is not null ? new Uri(path) : new Uri(url);
            var bitmap = new BitmapImage();
            if (DecodePixelWidth > 0)
            {
                bitmap.DecodePixelType = DecodePixelType.Physical;
                bitmap.DecodePixelWidth = DecodePixelWidth;
            }
            else if (ActualWidth > 0)
            {
                // Logical pixels: the image host scales them by the display's scale factor.
                _decodedWidth = Math.Ceiling(ActualWidth / DecodeStep) * DecodeStep;
                bitmap.DecodePixelType = DecodePixelType.Logical;
                bitmap.DecodePixelWidth = (int)_decodedWidth;
            }

            var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bitmap.ImageOpened += (_, _) => opened.TrySetResult(true);
            bitmap.ImageFailed += (_, _) => opened.TrySetResult(false);

            // Decoding needs the bitmap on an element in the tree; the hidden image hosts it until the fade. Attached
            // before its source is set, so without a decode size it is decoded at the size it is laid out at.
            var host = _showsA ? _b : _a;
            host.Source = bitmap;
            bitmap.UriSource = uri;

            var completed = await Task.WhenAny(opened.Task, Task.Delay(OpenTimeout));
            if (completed == opened.Task && !opened.Task.Result)
            {
                if (path is not null)
                {
                    Cache?.Remove(url);
                }

                return null;
            }

            return bitmap;
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Artwork {Url} could not be loaded", url);
            return null;
        }
    }

    private void Show(Image? incoming, Image outgoing, int version, bool animate)
    {
        if (version != _version)
        {
            return;
        }

        _fade?.Stop();
        if (incoming is not null)
        {
            _showsA = ReferenceEquals(incoming, _a);
        }

        if (!animate)
        {
            if (incoming is not null)
            {
                incoming.Opacity = 1;
            }

            outgoing.Opacity = 0;
            outgoing.Source = null;
            return;
        }

        var storyboard = new Storyboard();
        if (incoming is not null)
        {
            storyboard.Children.Add(Fade(incoming, 1));
        }

        storyboard.Children.Add(Fade(outgoing, 0));
        storyboard.Completed += (_, _) =>
        {
            // The outgoing art is invisible now: let go of it (unless a newer load already reuses that image).
            if (version == _version && ReferenceEquals(_fade, storyboard))
            {
                outgoing.Source = null;
            }
        };
        _fade = storyboard;
        storyboard.Begin();

        DoubleAnimation Fade(UIElement target, double to)
        {
            var animation = new DoubleAnimation
            {
                To = to,
                Duration = FadeDuration,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, nameof(UIElement.Opacity));
            return animation;
        }
    }
}
