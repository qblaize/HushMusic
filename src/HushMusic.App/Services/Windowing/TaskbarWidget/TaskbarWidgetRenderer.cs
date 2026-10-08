using System.Runtime.InteropServices;
using Microsoft.UI;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Services;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>Hover, press and volume feedback of the taskbar player (widget thread state).</summary>
/// <param name="OverlayVolume">Non-null while the wheel changes the volume: shown instead of the text.</param>
/// <param name="LightTaskbar">The taskbar (Windows "system mode") is light.</param>
internal readonly record struct WidgetVisuals(TaskbarWidgetPart Hover, TaskbarWidgetPart Pressed, double? OverlayVolume, bool OverlayMuted, bool LightTaskbar);

/// <summary>
/// Draws the taskbar player with GDI+ into a premultiplied BGRA buffer (a DIB section for UpdateLayeredWindow, or a
/// plain array for the test dumps). Text is grayscale-antialiased, because ClearType can't produce per-pixel alpha.
/// Fonts match the taskbar: Segoe UI Variable for text, Segoe Fluent Icons for glyphs. One thread at a time.
/// </summary>
internal sealed class TaskbarWidgetRenderer : IDisposable
{
    // Every pixel keeps at least this alpha, so the whole rectangle takes the mouse (alpha 0 would click through).
    private const uint HitTestFill = 0x01000000;

    private const char PreviousGlyph = '';
    private const char NextGlyph = '';
    private const char PlayGlyph = '';
    private const char PauseGlyph = '';
    private const char MusicGlyph = '';
    private const char MuteGlyph = '';

    private static readonly string[] SemiboldFamilies = ["Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI"];
    private static readonly string[] RegularFamilies = ["Segoe UI Variable Text", "Segoe UI"];
    private static readonly string[] IconFamilies = ["Segoe Fluent Icons", "Segoe MDL2 Assets"];

    private readonly Dictionary<string, IntPtr> _families = new(StringComparer.Ordinal);
    private Fonts? _fonts;
    private IntPtr _brush;
    private IntPtr _path;
    private IntPtr _textFormat;
    private IntPtr _centerFormat;
    private bool _disposed;

    /// <summary>Draws one frame into <paramref name="scan0"/> (top-down, <paramref name="stride"/> bytes per row).</summary>
    public void Render(IntPtr scan0, int stride, TaskbarWidgetGeometry g, TaskbarWidgetState state, WidgetVisuals visuals)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        EnsureResources(g.Scale);
        if (GdiPlus.GdipCreateBitmapFromScan0(g.Width, g.Height, stride, GdiPlus.PixelFormat32bppPArgb, scan0, out var bitmap) != 0)
        {
            return;
        }

        try
        {
            if (GdiPlus.GdipGetImageGraphicsContext(bitmap, out var graphics) != 0)
            {
                return;
            }

            try
            {
                Draw(graphics, g, state, visuals);
            }
            finally
            {
                GdiPlus.GdipDeleteGraphics(graphics);
            }
        }
        finally
        {
            GdiPlus.GdipDisposeImage(bitmap);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _fonts?.Dispose();
        foreach (var family in _families.Values)
        {
            GdiPlus.GdipDeleteFontFamily(family);
        }

        _families.Clear();
        Delete(ref _brush, GdiPlus.GdipDeleteBrush);
        Delete(ref _path, GdiPlus.GdipDeletePath);
        Delete(ref _textFormat, GdiPlus.GdipDeleteStringFormat);
        Delete(ref _centerFormat, GdiPlus.GdipDeleteStringFormat);
    }

    private static void Delete(ref IntPtr handle, Func<IntPtr, int> delete)
    {
        if (handle != IntPtr.Zero)
        {
            delete(handle);
            handle = IntPtr.Zero;
        }
    }

    private void Draw(IntPtr graphics, TaskbarWidgetGeometry g, TaskbarWidgetState state, WidgetVisuals visuals)
    {
        var palette = Palette.For(visuals.LightTaskbar);
        GdiPlus.GdipGraphicsClear(graphics, HitTestFill);
        GdiPlus.GdipSetSmoothingMode(graphics, GdiPlus.SmoothingModeAntiAlias);
        GdiPlus.GdipSetPixelOffsetMode(graphics, GdiPlus.PixelOffsetModeHalf);
        GdiPlus.GdipSetInterpolationMode(graphics, GdiPlus.InterpolationModeHighQualityBicubic);

        // Hovering anywhere lights the whole player like a taskbar button; the button under the pointer gets a second layer.
        if (visuals.Hover != TaskbarWidgetPart.None || visuals.Pressed != TaskbarWidgetPart.None)
        {
            FillRounded(graphics, g.Highlight, g.CornerRadius, palette.HoverFill);
        }

        DrawCover(graphics, g, state, palette);
        if (visuals.OverlayVolume is { } volume)
        {
            DrawVolume(graphics, g, volume, visuals.OverlayMuted, state.Accent, palette);
        }
        else
        {
            DrawText(graphics, g, state, palette);
        }

        DrawButton(graphics, g.Previous, g.ButtonRadius, TaskbarWidgetPart.Previous, PreviousGlyph, _fonts!.SmallGlyph, visuals, palette);
        DrawButton(graphics, g.PlayPause, g.ButtonRadius, TaskbarWidgetPart.PlayPause, state.IsPlaying ? PauseGlyph : PlayGlyph, _fonts.Glyph, visuals, palette);
        DrawButton(graphics, g.Next, g.ButtonRadius, TaskbarWidgetPart.Next, NextGlyph, _fonts.SmallGlyph, visuals, palette);
    }

    private void DrawCover(IntPtr graphics, TaskbarWidgetGeometry g, TaskbarWidgetState state, Palette palette)
    {
        if (state.Art is { } art && art.Pixels.Length >= art.Size * art.Size * 4)
        {
            var handle = GCHandle.Alloc(art.Pixels, GCHandleType.Pinned);
            try
            {
                if (GdiPlus.GdipCreateBitmapFromScan0(art.Size, art.Size, art.Size * 4, GdiPlus.PixelFormat32bppPArgb, handle.AddrOfPinnedObject(), out var image) == 0)
                {
                    GdiPlus.GdipDrawImageRectI(graphics, image, g.Cover.X, g.Cover.Y, g.Cover.Width, g.Cover.Height);
                    GdiPlus.GdipDisposeImage(image);
                }
            }
            finally
            {
                handle.Free();
            }

            // A hairline keeps dark covers from melting into a dark taskbar (and light ones into a light one).
            StrokeRounded(graphics, g.Cover, g.CoverRadius, palette.CoverStroke, Math.Max(1f, (float)g.Scale));
            return;
        }

        FillRounded(graphics, g.Cover, g.CoverRadius, palette.Placeholder);
        DrawGlyph(graphics, MusicGlyph, _fonts!.Glyph, g.Cover, palette.Secondary);
    }

    private void DrawText(IntPtr graphics, TaskbarWidgetGeometry g, TaskbarWidgetState state, Palette palette)
    {
        var fonts = _fonts!;
        if (g.Text.Width <= 0)
        {
            return;
        }

        // Two baselines placed so the text block sits centred on the cover (cap top to descender).
        var titleBaseline = g.Cover.Y + (float)(12.5 * g.Scale);
        var artistBaseline = titleBaseline + (float)(15 * g.Scale);
        DrawLine(graphics, state.Title, fonts.Title, g.Text.X, g.Text.Width, titleBaseline, palette.Primary);

        var artistX = (float)g.Text.X;
        if (state.IsLive)
        {
            // A small accent dot and "LIVE" ahead of the artist line (live streams have no progress to show).
            var accent = Palette.TuneAccent(state.Accent, palette.Light);
            var dot = (float)(5 * g.Scale);
            var dotY = artistBaseline - fonts.Live.CapHeight / 2 - dot / 2;
            SetBrush(accent);
            GdiPlus.GdipFillEllipse(graphics, _brush, artistX, dotY, dot, dot);
            artistX += dot + (float)(4 * g.Scale);
            var live = "LIVE";
            var liveWidth = Measure(graphics, live, fonts.Live);
            DrawLine(graphics, live, fonts.Live, artistX, liveWidth + 2, artistBaseline, accent);
            artistX += liveWidth + (float)(6 * g.Scale);
        }

        var artistWidth = g.Text.Right - artistX;
        if (artistWidth > 4 && !string.IsNullOrEmpty(state.Artist))
        {
            DrawLine(graphics, state.Artist, fonts.Artist, artistX, artistWidth, artistBaseline, palette.Secondary);
        }

        if (state.Progress is { } progress && !state.IsLive && g.Progress.Width > 0)
        {
            var track = g.Progress;
            FillRounded(graphics, track, track.Height / 2f, palette.Track);
            var filled = (int)Math.Round(track.Width * Math.Clamp(progress, 0, 1));
            if (filled > 0)
            {
                FillRounded(graphics, track with { Width = Math.Max(filled, track.Height) }, track.Height / 2f, Palette.TuneAccent(state.Accent, palette.Light));
            }
        }
    }

    private void DrawVolume(IntPtr graphics, TaskbarWidgetGeometry g, double volume, bool muted, uint accent, Palette palette)
    {
        var fonts = _fonts!;
        var level = Math.Clamp(volume, 0, 100);
        var glyph = muted || level <= 0 ? MuteGlyph : level < 34 ? '' : level < 67 ? '' : '';
        var size = (int)Math.Round(20 * g.Scale);
        var centerY = g.Cover.Y + (g.Cover.Height / 2);
        var glyphRect = new PixelRect(g.Text.X - (int)Math.Round(2 * g.Scale), centerY - (size / 2), size, size);
        DrawGlyph(graphics, glyph, fonts.Glyph, glyphRect, palette.Primary);

        var percent = $"{Math.Round(level):0}%";
        var percentWidth = Measure(graphics, "100%", fonts.Artist);
        var barLeft = glyphRect.Right + (int)Math.Round(6 * g.Scale);
        var barRight = g.Text.Right - (int)Math.Ceiling(percentWidth) - (int)Math.Round(8 * g.Scale);
        if (barRight - barLeft >= 8)
        {
            var height = Math.Max(2, (int)Math.Round(4 * g.Scale));
            var bar = new PixelRect(barLeft, centerY - (height / 2), barRight - barLeft, height);
            FillRounded(graphics, bar, height / 2f, palette.Track);
            var filled = (int)Math.Round(bar.Width * (muted ? 0 : level / 100));
            if (filled > 0)
            {
                FillRounded(graphics, bar with { Width = Math.Max(filled, height) }, height / 2f, Palette.TuneAccent(accent, palette.Light));
            }
        }

        var baseline = centerY + (fonts.Artist.CapHeight / 2);
        DrawLine(graphics, percent, fonts.Artist, g.Text.Right - percentWidth, percentWidth + 2, baseline, palette.Primary);
    }

    private void DrawButton(IntPtr graphics, PixelRect rect, int radius, TaskbarWidgetPart part, char glyph, FontInfo font, WidgetVisuals visuals, Palette palette)
    {
        var pressed = visuals.Pressed == part;
        if (pressed && visuals.Hover == part)
        {
            FillRounded(graphics, rect, radius, palette.PressedFill);
        }
        else if (visuals.Hover == part && visuals.Pressed == TaskbarWidgetPart.None)
        {
            FillRounded(graphics, rect, radius, palette.ButtonHoverFill);
        }

        DrawGlyph(graphics, glyph, font, rect, pressed ? palette.Secondary : palette.Primary);
    }

    private void DrawGlyph(IntPtr graphics, char glyph, FontInfo font, PixelRect rect, uint color)
    {
        GdiPlus.GdipSetTextRenderingHint(graphics, GdiPlus.TextRenderingHintAntiAlias);
        SetBrush(color);
        var layout = new GdiPlus.RectF(rect.X, rect.Y, rect.Width, rect.Height);
        GdiPlus.GdipDrawString(graphics, glyph.ToString(), 1, font.Handle, ref layout, _centerFormat, _brush);
    }

    // One line of text with an ellipsis, placed by its baseline.
    private void DrawLine(IntPtr graphics, string text, FontInfo font, float x, float width, float baseline, uint color)
    {
        GdiPlus.GdipSetTextRenderingHint(graphics, GdiPlus.TextRenderingHintAntiAlias);
        SetBrush(color);
        // A single line (no wrap), so the box can be taller than the line: GDI+ drops a trimmed line that doesn't fit.
        var layout = new GdiPlus.RectF(x, baseline - font.Ascent, width, font.LineHeight * 2);
        GdiPlus.GdipDrawString(graphics, text, text.Length, font.Handle, ref layout, _textFormat, _brush);
    }

    private float Measure(IntPtr graphics, string text, FontInfo font)
    {
        var layout = new GdiPlus.RectF(0, 0, 10_000, font.LineHeight);
        return GdiPlus.GdipMeasureString(graphics, text, text.Length, font.Handle, ref layout, _textFormat, out var bounds, out _, out _) == 0
            ? bounds.Width
            : text.Length * font.Size * 0.6f;
    }

    private void FillRounded(IntPtr graphics, PixelRect rect, float radius, uint color)
    {
        if (rect.IsEmpty || (color >> 24) == 0)
        {
            return;
        }

        SetBrush(color);
        BuildRoundedPath(rect.X, rect.Y, rect.Width, rect.Height, radius);
        GdiPlus.GdipFillPath(graphics, _brush, _path);
    }

    private void StrokeRounded(IntPtr graphics, PixelRect rect, float radius, uint color, float width)
    {
        if (rect.IsEmpty || GdiPlus.GdipCreatePen1(color, width, GdiPlus.UnitPixel, out var pen) != 0)
        {
            return;
        }

        var half = width / 2;
        BuildRoundedPath(rect.X + half, rect.Y + half, rect.Width - width, rect.Height - width, Math.Max(0, radius - half));
        GdiPlus.GdipDrawPath(graphics, pen, _path);
        GdiPlus.GdipDeletePen(pen);
    }

    private void BuildRoundedPath(float x, float y, float width, float height, float radius)
    {
        GdiPlus.GdipResetPath(_path);
        var r = Math.Min(radius, Math.Min(width, height) / 2);
        if (r <= 0.5f)
        {
            GdiPlus.GdipAddPathRectangle(_path, x, y, width, height);
            return;
        }

        var d = r * 2;
        GdiPlus.GdipAddPathArc(_path, x, y, d, d, 180, 90);
        GdiPlus.GdipAddPathArc(_path, x + width - d, y, d, d, 270, 90);
        GdiPlus.GdipAddPathArc(_path, x + width - d, y + height - d, d, d, 0, 90);
        GdiPlus.GdipAddPathArc(_path, x, y + height - d, d, d, 90, 90);
        GdiPlus.GdipClosePathFigure(_path);
    }

    private void SetBrush(uint color) => GdiPlus.GdipSetSolidFillColor(_brush, color);

    private void EnsureResources(double scale)
    {
        if (_brush == IntPtr.Zero)
        {
            GdiPlus.GdipCreateSolidFill(0xFFFFFFFF, out _brush);
            GdiPlus.GdipCreatePath(GdiPlus.FillModeWinding, out _path);
            GdiPlus.GdipStringFormatGetGenericTypographic(out var generic);
            GdiPlus.GdipCloneStringFormat(generic, out _textFormat);
            GdiPlus.GdipSetStringFormatFlags(_textFormat, GdiPlus.StringFormatFlagsNoWrap);
            GdiPlus.GdipSetStringFormatTrimming(_textFormat, GdiPlus.StringTrimmingEllipsisCharacter);
            GdiPlus.GdipCloneStringFormat(generic, out _centerFormat);
            GdiPlus.GdipSetStringFormatFlags(_centerFormat, GdiPlus.StringFormatFlagsNoWrap);
            GdiPlus.GdipSetStringFormatAlign(_centerFormat, GdiPlus.StringAlignmentCenter);
            GdiPlus.GdipSetStringFormatLineAlign(_centerFormat, GdiPlus.StringAlignmentCenter);
        }

        if (_fonts is { } fonts && Math.Abs(fonts.Scale - scale) < 0.001)
        {
            return;
        }

        _fonts?.Dispose();
        _fonts = new Fonts(
            scale,
            CreateFont(SemiboldFamilies, 12 * scale),
            CreateFont(RegularFamilies, 12 * scale),
            CreateFont(SemiboldFamilies, 9.5 * scale),
            CreateFont(IconFamilies, 14 * scale),
            CreateFont(IconFamilies, 12 * scale));
    }

    private FontInfo CreateFont(string[] families, double size)
    {
        foreach (var name in families)
        {
            if (!_families.TryGetValue(name, out var family))
            {
                if (GdiPlus.GdipCreateFontFamilyFromName(name, IntPtr.Zero, out family) != 0)
                {
                    continue;
                }

                _families[name] = family;
            }

            if (GdiPlus.GdipCreateFont(family, (float)size, GdiPlus.FontStyleRegular, GdiPlus.UnitPixel, out var font) != 0)
            {
                continue;
            }

            GdiPlus.GdipGetEmHeight(family, GdiPlus.FontStyleRegular, out var em);
            GdiPlus.GdipGetCellAscent(family, GdiPlus.FontStyleRegular, out var ascent);
            GdiPlus.GdipGetCellDescent(family, GdiPlus.FontStyleRegular, out var descent);
            var unit = em > 0 ? (float)size / em : 0;
            return new FontInfo(font, (float)size, ascent * unit, (ascent + descent) * unit, (float)size * 0.7f);
        }

        // GDI+ substitutes a generic sans-serif font for unknown names, so this is only reached if GDI+ itself fails.
        return new FontInfo(IntPtr.Zero, (float)size, (float)size, (float)size * 1.3f, (float)size * 0.7f);
    }

    /// <summary>Colours for a dark or light taskbar, as 0xAARRGGBB (GDI+ takes straight alpha).</summary>
    private readonly record struct Palette(
        bool Light,
        uint Primary,
        uint Secondary,
        uint HoverFill,
        uint ButtonHoverFill,
        uint PressedFill,
        uint Track,
        uint Placeholder,
        uint CoverStroke)
    {
        private static readonly Palette Dark = new(false, 0xFFFFFFFF, 0xC5FFFFFF, 0x0FFFFFFF, 0x12FFFFFF, 0x0AFFFFFF, 0x29FFFFFF, 0x1AFFFFFF, 0x14FFFFFF);
        private static readonly Palette LightPalette = new(true, 0xE4000000, 0x9E000000, 0x0D000000, 0x0F000000, 0x08000000, 0x24000000, 0x14000000, 0x14000000);

        public static Palette For(bool light) => light ? LightPalette : Dark;

        /// <summary>The accent, kept vivid and readable on the taskbar (lighter on dark, darker on light).</summary>
        public static uint TuneAccent(uint argb, bool light)
        {
            var color = ColorHelper.FromArgb(0xFF, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            var tuned = AccentPalette.MakeLegible(color, onLight: light);
            return 0xFF000000u | ((uint)tuned.R << 16) | ((uint)tuned.G << 8) | tuned.B;
        }
    }

    private sealed record FontInfo(IntPtr Handle, float Size, float Ascent, float LineHeight, float CapHeight);

    private sealed record Fonts(double Scale, FontInfo Title, FontInfo Artist, FontInfo Live, FontInfo Glyph, FontInfo SmallGlyph) : IDisposable
    {
        public void Dispose()
        {
            foreach (var font in new[] { Title, Artist, Live, Glyph, SmallGlyph })
            {
                if (font.Handle != IntPtr.Zero)
                {
                    GdiPlus.GdipDeleteFont(font.Handle);
                }
            }
        }
    }
}
