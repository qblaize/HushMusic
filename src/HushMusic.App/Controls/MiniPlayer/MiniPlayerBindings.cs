using HushMusic.Core.Models;

namespace HushMusic.App.Controls.MiniPlayer;

/// <summary>x:Bind function helpers for <see cref="MiniPlayerView"/>.</summary>
public static class MiniPlayerBindings
{
    public static string TitleOf(Track? track) => track?.Title ?? "Not playing";

    public static string ArtistsOf(Track? track) => track?.ArtistsText ?? string.Empty;

    /// <summary>The player's title (live-aware), or "Not playing".</summary>
    public static string Headline(bool hasTrack, string title) => hasTrack ? title : "Not playing";

    /// <summary>0 – 1 for the progress line's ScaleX.</summary>
    public static double Fraction(double position, double duration) =>
        duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
}
