using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Helpers;

/// <summary>
/// Attached property for album art: <c>helpers:ImageLoader.Url="{x:Bind Item.BestThumbnail.Url}"</c>
/// on an <see cref="Image"/>, <see cref="ImageBrush"/> or <see cref="PersonPicture"/>.
/// Images go through the disk cache (<see cref="IImageCache"/>) and fall back to a direct network load.
/// Optional <c>helpers:ImageLoader.DecodeWidth="56"</c> (logical pixels) limits decode memory.
/// </summary>
public static class ImageLoader
{
    public static readonly DependencyProperty UrlProperty = DependencyProperty.RegisterAttached(
        "Url",
        typeof(string),
        typeof(ImageLoader),
        new PropertyMetadata(null, OnUrlChanged));

    public static readonly DependencyProperty DecodeWidthProperty = DependencyProperty.RegisterAttached(
        "DecodeWidth",
        typeof(int),
        typeof(ImageLoader),
        new PropertyMetadata(0, OnDecodeWidthChanged));

    private static IImageCache? _cache;
    private static ILogger? _logger;
    private static bool _servicesResolved;

    public static string? GetUrl(DependencyObject element) => (string?)element.GetValue(UrlProperty);

    public static void SetUrl(DependencyObject element, string? value) => element.SetValue(UrlProperty, value);

    public static int GetDecodeWidth(DependencyObject element) => (int)element.GetValue(DecodeWidthProperty);

    public static void SetDecodeWidth(DependencyObject element, int value) => element.SetValue(DecodeWidthProperty, value);

    private static void OnUrlChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        _ = LoadAsync(d, e.NewValue as string);

    private static void OnDecodeWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (GetUrl(d) is { } url)
        {
            _ = LoadAsync(d, url);
        }
    }

    private static async Task LoadAsync(DependencyObject target, string? url)
    {
        try
        {
            // Clear right away so a recycled list container never shows the previous item's art.
            Apply(target, null);
            if (!ImageCache.TryNormalize(url, out var uri))
            {
                if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var other))
                {
                    Apply(target, CreateBitmap(target, other));
                }

                return;
            }

            string? localPath = null;
            if (ResolveCache() is { } cache)
            {
                try
                {
                    localPath = await cache.GetLocalPathAsync(uri.AbsoluteUri);
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Image cache miss for {Url}, loading from the network", uri);
                }
            }

            if (!IsStillWanted(target, url))
            {
                return;
            }

            if (localPath is null)
            {
                Apply(target, CreateBitmap(target, uri));
                return;
            }

            var bitmap = CreateBitmap(target, new Uri(localPath));
            bitmap.ImageFailed += (_, args) =>
            {
                // A corrupt cache entry: drop it and load straight from the network instead.
                _logger?.LogDebug("Cached image failed to decode ({Error}), falling back to {Url}", args.ErrorMessage, uri);
                _cache?.Remove(uri.AbsoluteUri);
                if (IsStillWanted(target, url))
                {
                    Apply(target, CreateBitmap(target, uri));
                }
            };
            Apply(target, bitmap);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Could not load image {Url}", url);
        }
    }

    private static BitmapImage CreateBitmap(DependencyObject target, Uri uri)
    {
        var bitmap = new BitmapImage();
        var width = GetDecodeWidth(target);
        if (width > 0)
        {
            bitmap.DecodePixelType = DecodePixelType.Logical;
            bitmap.DecodePixelWidth = width;
        }

        bitmap.UriSource = uri;
        return bitmap;
    }

    private static bool IsStillWanted(DependencyObject target, string? url) =>
        string.Equals(GetUrl(target), url, StringComparison.Ordinal);

    private static void Apply(DependencyObject target, ImageSource? source)
    {
        switch (target)
        {
            case Image image:
                image.Source = source;
                break;
            case ImageBrush brush:
                brush.ImageSource = source;
                break;
            case PersonPicture picture:
                picture.ProfilePicture = source;
                break;
        }
    }

    private static IImageCache? ResolveCache()
    {
        if (!_servicesResolved && App.Services is { } services)
        {
            _cache = services.GetService<IImageCache>();
            _logger = services.GetService<ILoggerFactory>()?.CreateLogger(typeof(ImageLoader).FullName!);
            _servicesResolved = true;
        }

        return _cache;
    }
}
