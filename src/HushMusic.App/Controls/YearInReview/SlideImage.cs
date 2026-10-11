using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace HushMusic.App.Controls.YearInReview;

/// <summary>Saves a recap slide as a PNG file.</summary>
internal static class SlideImage
{
    /// <summary>
    /// Draws <paramref name="element"/> and writes it to <paramref name="path"/> as a PNG <paramref name="pixelWidth"/>
    /// pixels wide (its shape kept). UI thread.
    /// </summary>
    /// <remarks>
    /// RenderTargetBitmap draws at the element's own rasterization scale (the display's, times the card's scale on
    /// screen), whatever size is asked for. So the element is drawn once more at the scale that gives the wanted width,
    /// which keeps text and edges sharp, and any small difference left is resampled while encoding.
    /// </remarks>
    public static async Task SaveAsync(UIElement element, int pixelWidth, string path)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0)
        {
            throw new InvalidOperationException("The slide could not be drawn.");
        }

        var scale = element.RasterizationScale;
        if (Math.Abs(bitmap.PixelWidth - pixelWidth) > 1)
        {
            try
            {
                element.RasterizationScale = scale * pixelWidth / bitmap.PixelWidth;
                bitmap = new RenderTargetBitmap();
                await bitmap.RenderAsync(element);
            }
            finally
            {
                element.RasterizationScale = scale;
            }
        }

        var pixels = await bitmap.GetPixelsAsync();
        var pixelHeight = (uint)Math.Round((double)pixelWidth * bitmap.PixelHeight / bitmap.PixelWidth);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels.ToArray());
        if (bitmap.PixelWidth != pixelWidth)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)pixelWidth;
            encoder.BitmapTransform.ScaledHeight = pixelHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();
        stream.Seek(0);
        await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await stream.AsStreamForRead().CopyToAsync(file);
    }
}
