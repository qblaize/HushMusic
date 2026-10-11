using HushMusic.Core.Abstractions;

namespace HushMusic.Playback;

/// <summary>
/// Which video stream to show for a size on screen. Sizes are YouTube's quality steps, counted on the short side of the
/// picture ("720p" is 1280×720, and 720×1280 for an upright video).
/// </summary>
public static class VideoFormats
{
    /// <summary>Never above 1080p: the picture is shown at cover size, and decoding more is wasted.</summary>
    public const int MaxHeight = 1080;

    /// <summary>The usual cap: 720p is sharp at Now Playing's cover sizes.</summary>
    public const int DefaultMaxHeight = 720;

    private static readonly int[] Steps = [144, 240, 360, 480, 720, 1080];

    /// <summary>
    /// The smallest quality step that fills <paramref name="pixels"/> physical pixels (with 10% upscaling allowed), at most
    /// <paramref name="cap"/> (itself at most <see cref="MaxHeight"/>). Unknown sizes (0 or less) get the cap.
    /// </summary>
    public static int HeightFor(double pixels, int cap = DefaultMaxHeight)
    {
        cap = Math.Clamp(cap, Steps[0], MaxHeight);
        if (!(pixels > 0))
        {
            return cap;
        }

        var step = Steps.FirstOrDefault(s => s >= pixels * 0.9, MaxHeight);
        return Math.Min(step, cap);
    }

    /// <summary>
    /// The sharpest stream no taller than <paramref name="maxHeight"/> (the smallest one when all are taller). At equal
    /// size, 30 fps or less wins over 60 (half the decoding for a cover-sized picture), then the higher bitrate.
    /// </summary>
    public static ResolvedStream? Pick(IReadOnlyList<ResolvedStream> streams, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(streams);
        var sized = streams.Where(s => ShortSide(s) > 0).ToList();
        if (sized.Count == 0)
        {
            return null;
        }

        var fitting = sized.Where(s => ShortSide(s) <= maxHeight).ToList();
        if (fitting.Count == 0)
        {
            var smallest = sized.Min(ShortSide);
            fitting = [.. sized.Where(s => ShortSide(s) == smallest)];
        }

        return fitting
            .OrderByDescending(ShortSide)
            .ThenBy(s => s.FrameRate is > 31 ? 1 : 0)
            .ThenByDescending(s => s.BitrateKbps ?? 0)
            .First();
    }

    private static int ShortSide(ResolvedStream stream) => Math.Min(stream.Width ?? 0, stream.Height ?? 0);
}
