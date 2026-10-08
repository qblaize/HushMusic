using System.Text.RegularExpressions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.NowPlaying;

/// <summary>Artwork URLs for the Now Playing view.</summary>
public static partial class NowPlayingArt
{
    /// <summary>The size big art is requested at: about twice its biggest on-screen size (560), so it stays sharp at 200 % scaling.</summary>
    public const int HeroPixels = 1080;

    /// <summary>
    /// A large square art URL. InnerTube lists art up to 544 px, but tracks that came from search results carry only
    /// 60/120 px. Google's image host serves any size through the "=wN-hN" suffix (the web player asks for large art
    /// the same way), so the size is rewritten; use <see cref="Listed"/> as the fallback.
    /// </summary>
    public static string? Large(Track? track, int pixels)
    {
        var url = track?.BestThumbnail?.Url;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsResizableHost(uri.Host))
        {
            return url;
        }

        return SizeSuffix().IsMatch(url) ? SizeSuffix().Replace(url, $"=w{pixels}-h{pixels}", 1) : url;
    }

    /// <summary>The biggest art InnerTube listed for the track.</summary>
    public static string? Listed(Track? track) => track?.ThumbnailFor(544)?.Url;

    /// <summary>Small art for the blurred background (it is decoded at a few pixels anyway).</summary>
    public static string? Small(Track? track) => track?.ThumbnailFor(120)?.Url;

    private static bool IsResizableHost(string host) =>
        host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase)
        || host.EndsWith("ggpht.com", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"=w\d+-h\d+")]
    private static partial Regex SizeSuffix();
}
