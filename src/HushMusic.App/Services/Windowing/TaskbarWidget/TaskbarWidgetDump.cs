using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// Test-only (HUSHMUSIC_TEST_WIDGET_DUMP=&lt;folder&gt;): draws the taskbar player in a set of states over taskbar-like
/// backgrounds and saves PNG sheets, so its look can be checked without touching (or capturing) the real taskbar.
/// One row per state: playing, hover on each part, pressed, volume overlay, long text, no art, live; dark then light.
/// HUSHMUSIC_TEST_WIDGET_DUMP_ART may name an image file to use as the cover.
/// </summary>
internal static class TaskbarWidgetDump
{
    private const uint DarkTaskbar = 0xFF1F1F1F;
    private const uint LightTaskbar = 0xFFEEEEEE;
    private const int Margin = 12;

    public static Task RunAsync(string folder, string? artPath, ILogger logger) => Task.Run(async () =>
    {
        try
        {
            Directory.CreateDirectory(folder);
            foreach (var scale in new[] { 1.0, 1.5 })
            {
                var height = (int)Math.Round(48 * scale);
                var width = (int)Math.Round(TaskbarWidgetLayout.PreferredWidth * scale);
                var geometry = TaskbarWidgetLayout.Measure(width, height, scale);
                var art = await LoadArtAsync(artPath, geometry.Cover.Width).ConfigureAwait(false);
                var rows = Rows(art).ToList();
                var sheet = RenderSheet(geometry, rows, out var sheetWidth, out var sheetHeight);
                var name = $"taskbar-widget-{scale * 100:0}";
                Save(Path.Combine(folder, name + ".png"), sheet, sheetWidth, sheetHeight);
                if (scale == 1.0)
                {
                    var zoomed = Zoom(sheet, sheetWidth, sheetHeight, 3);
                    Save(Path.Combine(folder, name + "-x3.png"), zoomed, sheetWidth * 3, sheetHeight * 3);
                }
            }

            logger.LogInformation("Taskbar player dump written to {Folder}", folder);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Taskbar player dump failed");
        }
    });

    private static IEnumerable<(TaskbarWidgetState State, WidgetVisuals Visuals, bool Checkered)> Rows(WidgetArt? art)
    {
        var playing = new TaskbarWidgetState(true, "Midnight City", "M83", true, 0.35, false, 45, false, 0xFF7A5CFA, art);
        var paused = playing with { IsPlaying = false, Progress = 0.62 };
        var longText = playing with
        {
            Title = "A Very Long Song Title That Will Never Fit (Extended Remastered Version)",
            Artist = "Somebody, Somebody Else & The Featured Orchestra",
        };
        var noArt = playing with { Art = null, Accent = 0xFF9D7BFA };
        var live = playing with { Title = "Radio Paradise – Main Mix", Artist = "Eclectic, commercial free", Progress = null, IsLive = true, Accent = 0xFF30B0C7 };
        foreach (var light in new[] { false, true })
        {
            WidgetVisuals V(TaskbarWidgetPart hover = TaskbarWidgetPart.None, TaskbarWidgetPart pressed = TaskbarWidgetPart.None, double? volume = null, bool muted = false) =>
                new(hover, pressed, volume, muted, light);

            yield return (playing, V(), false);
            yield return (paused, V(TaskbarWidgetPart.Content), false);
            yield return (playing, V(TaskbarWidgetPart.Previous), false);
            yield return (playing, V(TaskbarWidgetPart.PlayPause, TaskbarWidgetPart.PlayPause), false);
            yield return (playing, V(TaskbarWidgetPart.Next), false);
            yield return (playing, V(TaskbarWidgetPart.Content, volume: 45), false);
            yield return (playing, V(TaskbarWidgetPart.Content, volume: 0, muted: true), false);
            yield return (longText, V(), false);
            yield return (noArt, V(), false);
            yield return (live, V(), false);
            yield return (playing, V(TaskbarWidgetPart.Content), true);
        }
    }

    private static byte[] RenderSheet(TaskbarWidgetGeometry g, List<(TaskbarWidgetState State, WidgetVisuals Visuals, bool Checkered)> rows, out int width, out int height)
    {
        width = g.Width + (2 * Margin);
        var rowHeight = g.Height + Margin;
        height = (rows.Count * rowHeight) + Margin;
        var sheet = new byte[width * height * 4];
        var widget = new byte[g.Width * g.Height * 4];
        if (!GdiPlus.Acquire())
        {
            throw new InvalidOperationException("GDI+ did not start");
        }

        try
        {
            using var renderer = new TaskbarWidgetRenderer();
            for (var i = 0; i < rows.Count; i++)
            {
                var (state, visuals, checkered) = rows[i];
                var top = (i * rowHeight) + (Margin / 2);
                FillBackground(sheet, width, top, rowHeight, visuals.LightTaskbar, checkered);

                Array.Clear(widget);
                var handle = GCHandle.Alloc(widget, GCHandleType.Pinned);
                try
                {
                    renderer.Render(handle.AddrOfPinnedObject(), g.Width * 4, g, state, visuals);
                }
                finally
                {
                    handle.Free();
                }

                Composite(sheet, width, widget, g.Width, g.Height, Margin, top + (Margin / 2));
            }
        }
        finally
        {
            GdiPlus.Release();
        }

        return sheet;
    }

    private static void FillBackground(byte[] sheet, int width, int top, int rows, bool light, bool checkered)
    {
        for (var y = top; y < top + rows && y * width * 4 < sheet.Length; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var color = light ? LightTaskbar : DarkTaskbar;
                if (checkered && ((x / 8) + (y / 8)) % 2 == 0)
                {
                    color = light ? 0xFFD0D0D0 : 0xFF3A3A3A;
                }

                var i = ((y * width) + x) * 4;
                sheet[i] = (byte)color;
                sheet[i + 1] = (byte)(color >> 8);
                sheet[i + 2] = (byte)(color >> 16);
                sheet[i + 3] = 0xFF;
            }
        }
    }

    // Premultiplied "over", the same blend UpdateLayeredWindow does on the taskbar.
    private static void Composite(byte[] sheet, int sheetWidth, byte[] widget, int width, int height, int left, int top)
    {
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var s = ((y * width) + x) * 4;
                var d = (((top + y) * sheetWidth) + left + x) * 4;
                var inverse = 255 - widget[s + 3];
                for (var c = 0; c < 3; c++)
                {
                    sheet[d + c] = (byte)Math.Min(255, widget[s + c] + ((sheet[d + c] * inverse) + 127) / 255);
                }
            }
        }
    }

    private static byte[] Zoom(byte[] source, int width, int height, int factor)
    {
        var zoomed = new byte[source.Length * factor * factor];
        for (var y = 0; y < height * factor; y++)
        {
            for (var x = 0; x < width * factor; x++)
            {
                Buffer.BlockCopy(source, (((y / factor) * width) + (x / factor)) * 4, zoomed, ((y * width * factor) + x) * 4, 4);
            }
        }

        return zoomed;
    }

    private static void Save(string path, byte[] pixels, int width, int height)
    {
        if (!GdiPlus.Acquire())
        {
            return;
        }

        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            if (GdiPlus.GdipCreateBitmapFromScan0(width, height, width * 4, GdiPlus.PixelFormat32bppPArgb, handle.AddrOfPinnedObject(), out var bitmap) == 0)
            {
                var png = GdiPlus.PngEncoder;
                GdiPlus.GdipSaveImageToFile(bitmap, path, ref png, IntPtr.Zero);
                GdiPlus.GdipDisposeImage(bitmap);
            }
        }
        finally
        {
            handle.Free();
            GdiPlus.Release();
        }
    }

    private static async Task<WidgetArt?> LoadArtAsync(string? path, int size)
    {
        byte[] pixels;
        if (path is not null && File.Exists(path))
        {
            pixels = await WidgetArtLoader.DecodeAsync(path, size, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            // A diagonal gradient stands in for a cover.
            pixels = new byte[size * size * 4];
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var i = ((y * size) + x) * 4;
                    var t = (x + y) / (2.0 * size);
                    pixels[i] = (byte)(200 - (120 * t));
                    pixels[i + 1] = (byte)(60 + (60 * t));
                    pixels[i + 2] = (byte)(90 + (150 * t));
                    pixels[i + 3] = 0xFF;
                }
            }
        }

        WidgetArtLoader.RoundCorners(pixels, size, Math.Round(size / 8.0));
        return new WidgetArt(path ?? "synthetic", size, pixels);
    }
}
