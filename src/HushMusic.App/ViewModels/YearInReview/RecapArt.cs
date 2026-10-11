using System.Text.RegularExpressions;
using Windows.Graphics.Imaging;
using Windows.UI;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.ViewModels.YearInReview;

/// <summary>Artwork for the recap: bigger versions of the logged covers, and the accent colour of the top song's.</summary>
public static partial class RecapArt
{
    private const uint SampleSize = 40;

    /// <summary>
    /// The play log keeps 120 px covers. Google's image host serves any size through the "=wN-hN" suffix, so the size is
    /// rewritten for the big covers; other hosts (video thumbnails, station logos) are left as they are.
    /// </summary>
    public static string? Sized(string? url, int pixels)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !(uri.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith("ggpht.com", StringComparison.OrdinalIgnoreCase)))
        {
            return url;
        }

        return SizeSuffix().IsMatch(url) ? SizeSuffix().Replace(url, $"=w{pixels}-h{pixels}", 1) : url;
    }

    /// <summary>The cover's most prominent vibrant colour, made legible on the dark recap; null for grey or dark art.</summary>
    public static async Task<Color?> AccentOfAsync(IImageCache images, string? url, CancellationToken cancellationToken)
    {
        if (url is null || await images.GetLocalPathAsync(url, cancellationToken).ConfigureAwait(false) is not { } path)
        {
            return null;
        }

        byte[] pixels;
        using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
        using (var stream = file.AsRandomAccessStream())
        {
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);
            var transform = new BitmapTransform { ScaledWidth = SampleSize, ScaledHeight = SampleSize, InterpolationMode = BitmapInterpolationMode.Fant };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
            pixels = data.DetachPixelData();
        }

        return AccentPalette.PickVibrant(pixels) is { } vibrant ? AccentPalette.MakeLegible(vibrant) : null;
    }

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex SizeSuffix();
}
