using System.Runtime.InteropServices;

namespace HushMusic.App.Services.Windowing.TaskbarWidget;

/// <summary>
/// The GDI+ flat API (gdiplus.dll ships with Windows), enough to draw the taskbar player into a premultiplied 32-bpp
/// buffer: antialiased paths, grayscale-antialiased text and bitmaps, plus PNG output for the test dumps.
/// Every call returns a GDI+ Status (0 = Ok).
/// </summary>
internal static class GdiPlus
{
    public const int PixelFormat32bppPArgb = 0x000E200B;
    public const int UnitPixel = 2;
    public const int FontStyleRegular = 0;
    public const int SmoothingModeAntiAlias = 4;
    public const int PixelOffsetModeHalf = 4;
    public const int TextRenderingHintAntiAlias = 4;
    public const int InterpolationModeHighQualityBicubic = 7;
    public const int StringAlignmentCenter = 1;
    public const int StringFormatFlagsNoWrap = 0x1000;
    public const int StringTrimmingEllipsisCharacter = 3;
    public const int FillModeWinding = 1;

    private static readonly Lock Gate = new();
    private static IntPtr s_token;
    private static int s_users;

    /// <summary>PNG encoder CLSID (image/png).</summary>
    public static readonly Guid PngEncoder = new("557CF406-1A04-11D3-9A73-0000F81EF32E");

    /// <summary>Starts GDI+ for the process (reference counted). Returns false when it can't start.</summary>
    public static bool Acquire()
    {
        lock (Gate)
        {
            if (s_users == 0)
            {
                var input = new StartupInput { Version = 1 };
                if (GdiplusStartup(out s_token, ref input, IntPtr.Zero) != 0)
                {
                    s_token = IntPtr.Zero;
                    return false;
                }
            }

            s_users++;
            return true;
        }
    }

    public static void Release()
    {
        lock (Gate)
        {
            if (s_users == 0)
            {
                return;
            }

            s_users--;
            if (s_users == 0 && s_token != IntPtr.Zero)
            {
                GdiplusShutdown(s_token);
                s_token = IntPtr.Zero;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF(float x, float y, float width, float height)
    {
        public float X = x;
        public float Y = y;
        public float Width = width;
        public float Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInput
    {
        public uint Version;
        public IntPtr DebugEventCallback;
        public int SuppressBackgroundThread;
        public int SuppressExternalCodecs;
    }

    [DllImport("gdiplus.dll")]
    private static extern int GdiplusStartup(out IntPtr token, ref StartupInput input, IntPtr output);

    [DllImport("gdiplus.dll")]
    private static extern void GdiplusShutdown(IntPtr token);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateBitmapFromScan0(int width, int height, int stride, int format, IntPtr scan0, out IntPtr bitmap);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDisposeImage(IntPtr image);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipSaveImageToFile(IntPtr image, string fileName, ref Guid encoder, IntPtr parameters);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGetImageGraphicsContext(IntPtr image, out IntPtr graphics);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteGraphics(IntPtr graphics);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGraphicsClear(IntPtr graphics, uint argb);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetSmoothingMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetPixelOffsetMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetTextRenderingHint(IntPtr graphics, int hint);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetInterpolationMode(IntPtr graphics, int mode);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateSolidFill(uint argb, out IntPtr brush);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetSolidFillColor(IntPtr brush, uint argb);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteBrush(IntPtr brush);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreatePen1(uint argb, float width, int unit, out IntPtr pen);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeletePen(IntPtr pen);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreatePath(int fillMode, out IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeletePath(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipResetPath(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipAddPathArc(IntPtr path, float x, float y, float width, float height, float startAngle, float sweepAngle);

    [DllImport("gdiplus.dll")]
    public static extern int GdipAddPathRectangle(IntPtr path, float x, float y, float width, float height);

    [DllImport("gdiplus.dll")]
    public static extern int GdipClosePathFigure(IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipFillPath(IntPtr graphics, IntPtr brush, IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawPath(IntPtr graphics, IntPtr pen, IntPtr path);

    [DllImport("gdiplus.dll")]
    public static extern int GdipFillEllipse(IntPtr graphics, IntPtr brush, float x, float y, float width, float height);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDrawImageRectI(IntPtr graphics, IntPtr image, int x, int y, int width, int height);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipCreateFontFamilyFromName(string name, IntPtr collection, out IntPtr family);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteFontFamily(IntPtr family);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCreateFont(IntPtr family, float emSize, int style, int unit, out IntPtr font);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteFont(IntPtr font);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGetEmHeight(IntPtr family, int style, out ushort emHeight);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGetCellAscent(IntPtr family, int style, out ushort ascent);

    [DllImport("gdiplus.dll")]
    public static extern int GdipGetCellDescent(IntPtr family, int style, out ushort descent);

    [DllImport("gdiplus.dll")]
    public static extern int GdipStringFormatGetGenericTypographic(out IntPtr format);

    [DllImport("gdiplus.dll")]
    public static extern int GdipCloneStringFormat(IntPtr format, out IntPtr clone);

    [DllImport("gdiplus.dll")]
    public static extern int GdipDeleteStringFormat(IntPtr format);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatFlags(IntPtr format, int flags);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatAlign(IntPtr format, int align);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatLineAlign(IntPtr format, int align);

    [DllImport("gdiplus.dll")]
    public static extern int GdipSetStringFormatTrimming(IntPtr format, int trimming);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipDrawString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layout, IntPtr format, IntPtr brush);

    [DllImport("gdiplus.dll", CharSet = CharSet.Unicode)]
    public static extern int GdipMeasureString(IntPtr graphics, string text, int length, IntPtr font, ref RectF layout, IntPtr format, out RectF bounds, out int codepointsFitted, out int linesFilled);
}
