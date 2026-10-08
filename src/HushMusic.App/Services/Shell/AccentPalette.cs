using Microsoft.UI;
using Windows.UI;

namespace HushMusic.App.Services.Shell;

/// <summary>Colour maths for the album-art accent: dominant vibrant colour, legibility clamp, foreground choice. Pure and thread-safe.</summary>
public static class AccentPalette
{
    private const int HueBins = 36;

    /// <summary>Minimum relative luminance so accent text stays readable on #0B0B0C (about 4.7:1).</summary>
    private const double MinLuminance = 0.2;

    /// <summary>Maximum relative luminance so accent text stays readable on #FFFFFF (4.6:1) and #F5F5F7 (4.2:1), and white text on the accent fill too.</summary>
    private const double MaxLuminanceOnLight = 0.18;

    /// <summary>
    /// Picks the most prominent vibrant colour from straight-alpha BGRA pixels. Near-black, near-white and grey pixels are
    /// ignored; the rest are bucketed by hue and weighted towards saturated, mid-lightness pixels.
    /// Returns null when too few pixels qualify (greyscale or very dark art).
    /// </summary>
    public static Color? PickVibrant(ReadOnlySpan<byte> bgra)
    {
        Span<double> weight = stackalloc double[HueBins];
        Span<double> red = stackalloc double[HueBins];
        Span<double> green = stackalloc double[HueBins];
        Span<double> blue = stackalloc double[HueBins];
        var total = 0;
        var qualifying = 0;

        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
            {
                continue;
            }

            total++;
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var chroma = max - min;
            var lightness = (max + min) / 2;
            if (lightness < 0.1 || lightness > 0.92 || chroma < 0.1)
            {
                continue;
            }

            var saturation = chroma / (1 - Math.Abs((2 * lightness) - 1));
            if (saturation < 0.22)
            {
                continue;
            }

            var bin = (int)(Hue(r, g, b, max, chroma) / (360.0 / HueBins)) % HueBins;
            var w = saturation * chroma * Math.Max(0.15, 1 - (Math.Abs(lightness - 0.55) * 1.6));
            weight[bin] += w;
            red[bin] += r * w;
            green[bin] += g * w;
            blue[bin] += b * w;
            qualifying++;
        }

        if (qualifying < 12 || qualifying < total * 0.03)
        {
            return null;
        }

        // Score each bin together with its neighbours so a hue split across two bins still wins.
        var best = -1;
        var bestScore = 0.0;
        for (var i = 0; i < HueBins; i++)
        {
            var score = weight[i] + (0.5 * (weight[(i + HueBins - 1) % HueBins] + weight[(i + 1) % HueBins]));
            if (score > bestScore)
            {
                bestScore = score;
                best = i;
            }
        }

        if (best < 0)
        {
            return null;
        }

        double sw = 0, sr = 0, sg = 0, sb = 0;
        ReadOnlySpan<int> window = [(best + HueBins - 1) % HueBins, best, (best + 1) % HueBins];
        foreach (var bin in window)
        {
            sw += weight[bin];
            sr += red[bin];
            sg += green[bin];
            sb += blue[bin];
        }

        return sw <= 0 ? null : FromRgb(sr / sw, sg / sw, sb / sw);
    }

    /// <summary>Keeps the hue but makes the colour vivid enough and light enough to read on the dark background.</summary>
    public static Color MakeLegible(Color color)
    {
        ToHsl(color, out var h, out var s, out var l);

        // Saturation up a little: the luminance floor below lightens the colour, which would otherwise read as pastel.
        s = Math.Clamp(s * 1.15, 0.6, 0.95);
        l = Math.Clamp(l, 0.48, 0.72);
        var result = FromHsl(h, s, l);
        while (RelativeLuminance(result) < MinLuminance && l < 0.82)
        {
            l += 0.02;
            result = FromHsl(h, s, l);
        }

        return result;
    }

    /// <summary>
    /// <see cref="MakeLegible(Color)"/> for the theme: on the light theme the colour is darkened instead, until it reads on
    /// white and light-grey surfaces.
    /// </summary>
    public static Color MakeLegible(Color color, bool onLight)
    {
        if (!onLight)
        {
            return MakeLegible(color);
        }

        ToHsl(color, out var h, out var s, out var l);
        s = Math.Clamp(s * 1.1, 0.55, 0.9);
        l = Math.Clamp(l, 0.3, 0.5);
        var result = FromHsl(h, s, l);
        while (RelativeLuminance(result) > MaxLuminanceOnLight && l > 0.16)
        {
            l -= 0.02;
            result = FromHsl(h, s, l);
        }

        return result;
    }

    /// <summary>
    /// Text/glyph colour on an accent fill: white (Apple style) while it keeps at least 3:1 contrast, near-black on
    /// lighter accents (bright orange, icy blue), where white would wash out.
    /// </summary>
    public static Color ForegroundFor(Color accent) =>
        RelativeLuminance(accent) > 0.3 ? ColorHelper.FromArgb(0xFF, 0x0B, 0x0B, 0x0C) : Colors.White;

    public static double RelativeLuminance(Color c) =>
        (0.2126 * Linear(c.R)) + (0.7152 * Linear(c.G)) + (0.0722 * Linear(c.B));

    public static Color Lerp(Color from, Color to, double t) => ColorHelper.FromArgb(
        LerpByte(from.A, to.A, t),
        LerpByte(from.R, to.R, t),
        LerpByte(from.G, to.G, t),
        LerpByte(from.B, to.B, t));

    public static Color WithAlpha(Color c, byte alpha) => ColorHelper.FromArgb(alpha, c.R, c.G, c.B);

    /// <summary>Opaque colour of <paramref name="tint"/> laid over <paramref name="background"/> at <paramref name="amount"/> (0–1).</summary>
    public static Color Over(Color tint, Color background, double amount) =>
        ColorHelper.FromArgb(0xFF, LerpByte(background.R, tint.R, amount), LerpByte(background.G, tint.G, amount), LerpByte(background.B, tint.B, amount));

    private static byte LerpByte(byte a, byte b, double t) => (byte)Math.Round(a + ((b - a) * t));

    private static double Linear(byte channel)
    {
        var c = channel / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Hue(double r, double g, double b, double max, double chroma)
    {
        double h;
        if (max == r)
        {
            h = (g - b) / chroma % 6;
        }
        else if (max == g)
        {
            h = ((b - r) / chroma) + 2;
        }
        else
        {
            h = ((r - g) / chroma) + 4;
        }

        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static void ToHsl(Color c, out double h, out double s, out double l)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var chroma = max - min;
        l = (max + min) / 2;
        if (chroma <= 0)
        {
            h = 0;
            s = 0;
            return;
        }

        s = chroma / (1 - Math.Abs((2 * l) - 1));
        h = Hue(r, g, b, max, chroma);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var chroma = (1 - Math.Abs((2 * l) - 1)) * s;
        var x = chroma * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = l - (chroma / 2);
        (double r, double g, double b) = (h % 360) switch
        {
            < 60 => (chroma, x, 0.0),
            < 120 => (x, chroma, 0.0),
            < 180 => (0.0, chroma, x),
            < 240 => (0.0, x, chroma),
            < 300 => (x, 0.0, chroma),
            _ => (chroma, 0.0, x),
        };
        return FromRgb(r + m, g + m, b + m);
    }

    private static Color FromRgb(double r, double g, double b) => ColorHelper.FromArgb(
        0xFF,
        (byte)Math.Round(Math.Clamp(r, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(g, 0, 1) * 255),
        (byte)Math.Round(Math.Clamp(b, 0, 1) * 255));
}
