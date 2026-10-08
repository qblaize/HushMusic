using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Windows.Graphics.Imaging;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Services.Radio;

/// <summary>What a station logo looks like, so it can be shown well: full-bleed, framed on a colour wash, or on a plate.</summary>
/// <param name="LocalPath">The cached file.</param>
/// <param name="Width">Natural width in pixels.</param>
/// <param name="Height">Natural height in pixels.</param>
/// <param name="IsOpaque">No (noticeable) transparency: the logo brings its own background.</param>
/// <param name="IsLight">The visible pixels are mostly light (a transparent light logo needs a dark plate).</param>
public sealed record StationLogoInfo(string LocalPath, int Width, int Height, bool IsOpaque, bool IsLight)
{
    public bool IsSquare => Width > 0 && Height > 0 && Math.Abs(Width - Height) <= Math.Max(Width, Height) * 0.15;
}

/// <summary>Downloads (through the image cache) and inspects station logos. Results are kept for the session.</summary>
public interface IStationLogos
{
    /// <summary>Null when the logo can't be loaded or decoded (e.g. SVG, HTML instead of an image).</summary>
    Task<StationLogoInfo?> GetAsync(string url, CancellationToken cancellationToken = default);
}

internal sealed class StationLogos(IImageCache images, ILogger<StationLogos> logger) : IStationLogos
{
    private const uint SampleSize = 24;

    private readonly ConcurrentDictionary<string, Lazy<Task<StationLogoInfo?>>> _results = new(StringComparer.Ordinal);

    public async Task<StationLogoInfo?> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        var lazy = _results.GetOrAdd(url, u => new Lazy<Task<StationLogoInfo?>>(() => Task.Run(() => AnalyzeAsync(u))));
        var info = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (info is null)
        {
            // Not remembered: a network hiccup shouldn't hide the logo for the rest of the session.
            _results.TryRemove(new KeyValuePair<string, Lazy<Task<StationLogoInfo?>>>(url, lazy));
        }

        return info;
    }

    private async Task<StationLogoInfo?> AnalyzeAsync(string url)
    {
        try
        {
            var path = await images.GetLocalPathAsync(url).ConfigureAwait(false);
            if (path is null)
            {
                return null;
            }

            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            using var stream = file.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = SampleSize, ScaledHeight = SampleSize, InterpolationMode = BitmapInterpolationMode.Fant };
            var data = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Straight,
                transform,
                ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var pixels = data.DetachPixelData();

            var transparent = 0;
            double luma = 0;
            double weight = 0;
            for (var i = 0; i + 3 < pixels.Length; i += 4)
            {
                var alpha = pixels[i + 3] / 255.0;
                if (alpha < 0.96)
                {
                    transparent++;
                }

                luma += alpha * ((0.0722 * pixels[i]) + (0.7152 * pixels[i + 1]) + (0.2126 * pixels[i + 2])) / 255.0;
                weight += alpha;
            }

            var count = pixels.Length / 4;
            return new StationLogoInfo(
                path,
                (int)decoder.OrientedPixelWidth,
                (int)decoder.OrientedPixelHeight,
                IsOpaque: transparent <= count * 0.02,
                IsLight: weight > 0 && luma / weight > 0.62);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Station logo {Url} could not be used", url);
            return null;
        }
    }
}
