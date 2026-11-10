namespace HushMusic.Core.Services;

/// <summary>
/// What shows behind the taskbar player, measured from a screen sample of its rectangle. The player draws on a clear
/// background, so on a see-through taskbar (or one a tool made transparent) the wallpaper shows through, and Windows'
/// light / dark mode says nothing about it.
/// </summary>
/// <param name="DarkShare">Share of the sampled pixels too dark for dark text (contrast under 3:1 with black).</param>
/// <param name="LightShare">Share of the sampled pixels too light for light text (contrast under 3:1 with white).</param>
/// <param name="Samples">How many pixels were measured.</param>
public readonly record struct BackdropReading(double DarkShare, double LightShare, int Samples);

/// <summary>Reads a backdrop sample and picks the text colour (and a shadow) that stays readable on it.</summary>
public static class TaskbarBackdrop
{
    /// <summary>Pixels where the player itself draws more than this alpha are skipped (they show its own content).</summary>
    public const byte MaxOverlayAlpha = 8;

    // Relative luminance (WCAG) of a 3:1 contrast with black and with white.
    private const double TooDarkForDarkText = 0.10;
    private const double TooLightForLightText = 0.30;

    // The current colour stays until the other one does clearly better, so a busy backdrop can't make it flicker.
    private const double SwitchMargin = 0.15;

    // A shadow behind the text once this share of the backdrop is hard to read on: none on a plain taskbar.
    private const double ShadowShare = 0.04;

    private static readonly double[] Linear = BuildLinearTable();

    /// <summary>
    /// Measures <paramref name="screen"/> (BGRA rows, as captured) where <paramref name="overlay"/> (the player's own
    /// premultiplied BGRA pixels, same size, or empty) draws next to nothing. Null when too little of it is clear.
    /// </summary>
    public static BackdropReading? Read(ReadOnlySpan<byte> screen, ReadOnlySpan<byte> overlay)
    {
        if (screen.Length < 4 || (overlay.Length != 0 && overlay.Length != screen.Length))
        {
            return null;
        }

        var pixels = screen.Length / 4;
        int samples = 0, dark = 0, light = 0;
        for (var i = 0; i < pixels; i++)
        {
            var o = i * 4;
            var hasOverlay = overlay.Length != 0;
            var alpha = hasOverlay ? overlay[o + 3] : 0;
            if (alpha > MaxOverlayAlpha)
            {
                continue;
            }

            // Takes the player's faint fill back out: screen = overlay + (1 - alpha) * backdrop.
            var keep = 1 - (alpha / 255.0);
            var b = Unmix(screen[o], hasOverlay ? overlay[o] : (byte)0, keep);
            var g = Unmix(screen[o + 1], hasOverlay ? overlay[o + 1] : (byte)0, keep);
            var r = Unmix(screen[o + 2], hasOverlay ? overlay[o + 2] : (byte)0, keep);
            var luminance = (0.0722 * Linear[b]) + (0.7152 * Linear[g]) + (0.2126 * Linear[r]);
            samples++;
            if (luminance < TooDarkForDarkText)
            {
                dark++;
            }
            else if (luminance > TooLightForLightText)
            {
                light++;
            }
        }

        // At least a tenth of the rectangle, so a sample taken while the player is busy (hovered, a volume bar) is skipped.
        if (samples < Math.Max(64, pixels / 10))
        {
            return null;
        }

        return new BackdropReading((double)dark / samples, (double)light / samples, samples);
    }

    /// <summary>
    /// Whether the player should draw light text on <paramref name="reading"/>, given what it draws now
    /// (<paramref name="lightText"/>): it switches only when the other colour is readable on clearly more of the backdrop.
    /// </summary>
    public static bool UseLightText(BackdropReading reading, bool lightText) => lightText
        ? reading.LightShare <= reading.DarkShare + SwitchMargin
        : reading.DarkShare > reading.LightShare + SwitchMargin;

    /// <summary>A busy backdrop (a photo): parts of it are hard to read on even in the better colour.</summary>
    public static bool NeedsShadow(BackdropReading reading, bool lightText) =>
        (lightText ? reading.LightShare : reading.DarkShare) > ShadowShare;

    private static int Unmix(byte screen, byte overlay, double keep) => (int)Math.Clamp(Math.Round((screen - overlay) / keep), 0, 255);

    private static double[] BuildLinearTable()
    {
        var table = new double[256];
        for (var i = 0; i < table.Length; i++)
        {
            var c = i / 255.0;
            table[i] = c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return table;
    }
}
