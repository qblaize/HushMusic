using Windows.Graphics.Imaging;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// Album art for the taskbar player: the cached image, centre-cropped to a square, scaled to the exact pixel size
/// it is drawn at (WIC Fant) and given antialiased rounded corners, as premultiplied BGRA. Any thread.
/// </summary>
internal static class WidgetArtLoader
{
    public static async Task<WidgetArt?> LoadAsync(IImageCache images, string url, int size, double radius, CancellationToken cancellationToken)
    {
        var path = await images.GetLocalPathAsync(url, cancellationToken).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        var pixels = await DecodeAsync(path, size, cancellationToken).ConfigureAwait(false);
        RoundCorners(pixels, size, radius);
        return new WidgetArt(url, size, pixels);
    }

    public static async Task<byte[]> DecodeAsync(string path, int size, CancellationToken cancellationToken)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        using var stream = file.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(cancellationToken).ConfigureAwait(false);

        // Scale the short side to `size`, then keep the centre (video thumbnails are 16:9).
        var shortSide = Math.Max(1u, Math.Min(decoder.PixelWidth, decoder.PixelHeight));
        var scale = (double)size / shortSide;
        var width = Math.Max((uint)size, (uint)Math.Round(decoder.PixelWidth * scale));
        var height = Math.Max((uint)size, (uint)Math.Round(decoder.PixelHeight * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = width,
            ScaledHeight = height,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (width - (uint)size) / 2, Y = (height - (uint)size) / 2, Width = (uint)size, Height = (uint)size },
        };
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Premultiplied,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
        return data.DetachPixelData();
    }

    /// <summary>Fades the four corners of a premultiplied square image to a circle of <paramref name="radius"/> (4x4 supersampled).</summary>
    public static void RoundCorners(byte[] pixels, int size, double radius)
    {
        var r = Math.Min(radius, size / 2.0);
        var extent = (int)Math.Ceiling(r);
        if (extent <= 0 || pixels.Length < size * size * 4)
        {
            return;
        }

        for (var y = 0; y < extent; y++)
        {
            for (var x = 0; x < extent; x++)
            {
                var inside = 0;
                for (var sy = 0; sy < 4; sy++)
                {
                    for (var sx = 0; sx < 4; sx++)
                    {
                        var dx = r - (x + ((sx + 0.5) / 4));
                        var dy = r - (y + ((sy + 0.5) / 4));
                        if (dx <= 0 || dy <= 0 || (dx * dx) + (dy * dy) <= r * r)
                        {
                            inside++;
                        }
                    }
                }

                if (inside == 16)
                {
                    continue;
                }

                var coverage = inside / 16.0;
                Scale(pixels, size, x, y, coverage);
                Scale(pixels, size, size - 1 - x, y, coverage);
                Scale(pixels, size, x, size - 1 - y, coverage);
                Scale(pixels, size, size - 1 - x, size - 1 - y, coverage);
            }
        }
    }

    private static void Scale(byte[] pixels, int size, int x, int y, double coverage)
    {
        var i = ((y * size) + x) * 4;
        for (var c = 0; c < 4; c++)
        {
            pixels[i + c] = (byte)Math.Round(pixels[i + c] * coverage);
        }
    }
}
