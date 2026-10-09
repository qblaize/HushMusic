using System.Numerics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Storage;
using Windows.Storage.Streams;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Artwork that fades to transparent towards the bottom (a Composition gradient mask), so it melts into whatever is
/// behind the page, including the shell's ambient glow, instead of ending on a solid colour.
/// A small <see cref="DecodeSize"/> decodes the art at a few pixels and stretches it: a soft, heavily blurred colour wash.
/// Without one, the art is decoded no bigger than the control (artist banners come much larger than they are shown).
/// </summary>
public sealed partial class FadingArtwork : UserControl
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.Register(
        nameof(Url), typeof(string), typeof(FadingArtwork), new PropertyMetadata(null, (d, e) => ((FadingArtwork)d).OnUrlChanged()));

    public static readonly DependencyProperty DecodeSizeProperty = DependencyProperty.Register(
        nameof(DecodeSize), typeof(double), typeof(FadingArtwork), new PropertyMetadata(0.0));

    public static readonly DependencyProperty FadeFromProperty = DependencyProperty.Register(
        nameof(FadeFrom), typeof(double), typeof(FadingArtwork), new PropertyMetadata(0.5));

    public static readonly DependencyProperty TopOpacityProperty = DependencyProperty.Register(
        nameof(TopOpacity), typeof(double), typeof(FadingArtwork), new PropertyMetadata(1.0));

    public static readonly DependencyProperty VerticalAlignmentRatioProperty = DependencyProperty.Register(
        nameof(VerticalAlignmentRatio), typeof(double), typeof(FadingArtwork), new PropertyMetadata(0.5));

    private static readonly TimeSpan FadeInDuration = TimeSpan.FromMilliseconds(350);

    // The control's size is rounded up to this many pixels before decoding.
    private const double DecodeStep = 64;

    private readonly Border _host = new();
    private SpriteVisual? _sprite;
    private CompositionSurfaceBrush? _surfaceBrush;
    private LoadedImageSurface? _surface;
    private string? _loadedUrl;
    private int _version;
    private double _decodedPixels;

    public FadingArtwork()
    {
        IsHitTestVisible = false;
        IsTabStop = false;
        Content = _host;
        _host.SizeChanged += (_, e) =>
        {
            ResizeSprite(e.NewSize);

            // Grown past the size the art was decoded at (the window got bigger): decode it again, sharper.
            if (_decodedPixels > 0 && LaidOutPixels() > _decodedPixels && _loadedUrl is not null)
            {
                _ = LoadAsync(_loadedUrl, keepShown: true);
            }
        };
        Loaded += (_, _) =>
        {
            EnsureVisual();
            OnUrlChanged();
        };
        Unloaded += (_, _) => ReleaseSurface();
    }

    public string? Url
    {
        get => (string?)GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    /// <summary>Decode the image at most this many pixels wide/high (0 = the control's size). A few pixels give a blur.</summary>
    public double DecodeSize
    {
        get => (double)GetValue(DecodeSizeProperty);
        set => SetValue(DecodeSizeProperty, value);
    }

    /// <summary>Fraction of the height (0–1) where the fade to transparent begins.</summary>
    public double FadeFrom
    {
        get => (double)GetValue(FadeFromProperty);
        set => SetValue(FadeFromProperty, value);
    }

    /// <summary>Opacity (0–1) at the top edge, fading in over the first fifth; softens an edge against content above.</summary>
    public double TopOpacity
    {
        get => (double)GetValue(TopOpacityProperty);
        set => SetValue(TopOpacityProperty, value);
    }

    /// <summary>Which part of a cropped image stays visible: 0 = top, 0.5 = centre.</summary>
    public double VerticalAlignmentRatio
    {
        get => (double)GetValue(VerticalAlignmentRatioProperty);
        set => SetValue(VerticalAlignmentRatioProperty, value);
    }

    private void EnsureVisual()
    {
        if (_sprite is not null)
        {
            return;
        }

        var compositor = ElementCompositionPreview.GetElementVisual(_host).Compositor;
        _surfaceBrush = compositor.CreateSurfaceBrush();
        _surfaceBrush.Stretch = CompositionStretch.UniformToFill;
        _surfaceBrush.VerticalAlignmentRatio = (float)Math.Clamp(VerticalAlignmentRatio, 0, 1);
        _surfaceBrush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;

        var fadeFrom = (float)Math.Clamp(FadeFrom, 0, 0.95);
        var mask = compositor.CreateLinearGradientBrush();
        mask.StartPoint = Vector2.Zero;
        mask.EndPoint = new Vector2(0, 1);
        var top = (byte)Math.Round(Math.Clamp(TopOpacity, 0, 1) * 255);
        mask.ColorStops.Add(compositor.CreateColorGradientStop(0, ColorHelper.FromArgb(top, 0xFF, 0xFF, 0xFF)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(Math.Min(0.2f, fadeFrom), Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(fadeFrom, Colors.White));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(fadeFrom + ((1 - fadeFrom) * 0.5f), ColorHelper.FromArgb(0x59, 0xFF, 0xFF, 0xFF)));
        mask.ColorStops.Add(compositor.CreateColorGradientStop(1, ColorHelper.FromArgb(0, 0xFF, 0xFF, 0xFF)));

        var masked = compositor.CreateMaskBrush();
        masked.Source = _surfaceBrush;
        masked.Mask = mask;

        _sprite = compositor.CreateSpriteVisual();
        _sprite.Brush = masked;
        _sprite.Opacity = 0;
        ResizeSprite(new Size(_host.ActualWidth, _host.ActualHeight));
        ElementCompositionPreview.SetElementChildVisual(_host, _sprite);
    }

    private void ResizeSprite(Size size)
    {
        if (_sprite is not null)
        {
            _sprite.Size = new Vector2((float)size.Width, (float)size.Height);
        }
    }

    private void OnUrlChanged()
    {
        if (_sprite is null || Url == _loadedUrl)
        {
            return;
        }

        _ = LoadAsync(Url, keepShown: false);
    }

    // keepShown: the same art again at a bigger size; the current one stays up until it is swapped in.
    private async Task LoadAsync(string? url, bool keepShown)
    {
        var version = ++_version;
        _loadedUrl = url;
        if (!keepShown)
        {
            ReleaseSurface(keepUrl: true);
        }

        if (string.IsNullOrWhiteSpace(url) || _sprite is null || _surfaceBrush is null)
        {
            return;
        }

        try
        {
            using var stream = await OpenAsync(url);
            if (version != _version || stream is null)
            {
                return;
            }

            var maxSize = DecodeSize > 0 ? DecodeSize : LaidOutPixels();
            _decodedPixels = DecodeSize > 0 ? 0 : maxSize;
            var surface = maxSize > 0
                ? LoadedImageSurface.StartLoadFromStream(stream, new Size(maxSize, maxSize))
                : LoadedImageSurface.StartLoadFromStream(stream);
            var loaded = new TaskCompletionSource<bool>();
            surface.LoadCompleted += (_, e) => loaded.TrySetResult(e.Status == LoadedImageSourceLoadStatus.Success);
            if (!keepShown)
            {
                _surface = surface;
                _surfaceBrush.Surface = surface;
            }

            // The stream must stay open until the surface has read it.
            var success = await loaded.Task;
            if (version != _version)
            {
                if (keepShown)
                {
                    surface.Dispose();
                }

                return;
            }

            if (keepShown)
            {
                if (!success)
                {
                    surface.Dispose();
                    return;
                }

                _surface?.Dispose();
                _surface = surface;
                _surfaceBrush.Surface = surface;
            }
            else if (success)
            {
                FadeIn();
            }
        }
        catch (Exception ex)
        {
            Logger?.LogDebug(ex, "Artwork wash for {Url} could not be loaded", url);
        }
    }

    // The longer side of the control in physical pixels (the art is cropped to fill it, so this always covers it).
    private double LaidOutPixels()
    {
        var side = Math.Max(_host.ActualWidth, _host.ActualHeight) * (XamlRoot?.RasterizationScale ?? 1.0);
        return side > 0 ? Math.Ceiling(side / DecodeStep) * DecodeStep : 0;
    }

    private static async Task<IRandomAccessStream?> OpenAsync(string url)
    {
        // Same disk cache as every other artwork in the app; straight from the network if it can't be cached.
        if (App.Services?.GetService<IImageCache>() is { } cache && await cache.GetLocalPathAsync(url) is { } path)
        {
            return await FileRandomAccessStream.OpenAsync(path, FileAccessMode.Read);
        }

        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? await RandomAccessStreamReference.CreateFromUri(uri).OpenReadAsync()
            : null;
    }

    private void FadeIn()
    {
        if (_sprite is null)
        {
            return;
        }

        var animation = _sprite.Compositor.CreateScalarKeyFrameAnimation();
        animation.InsertKeyFrame(1, 1);
        animation.Duration = FadeInDuration;
        _sprite.StartAnimation(nameof(Visual.Opacity), animation);
    }

    private void ReleaseSurface() => ReleaseSurface(keepUrl: false);

    private void ReleaseSurface(bool keepUrl)
    {
        if (!keepUrl)
        {
            _loadedUrl = null;
            _version++;
        }

        if (_sprite is not null)
        {
            _sprite.StopAnimation(nameof(Visual.Opacity));
            _sprite.Opacity = 0;
        }

        if (_surfaceBrush is not null)
        {
            _surfaceBrush.Surface = null;
        }

        _surface?.Dispose();
        _surface = null;
    }

    private static ILogger? Logger => App.Services?.GetService<ILoggerFactory>()?.CreateLogger(typeof(FadingArtwork).FullName!);
}
