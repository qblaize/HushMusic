using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>Thumbnail lists. All sizes are kept; <see cref="Thumbnail.Largest"/> picks by area, not by order.</summary>
internal static class Thumbnails
{
    /// <summary>A raw <c>[{url, width, height}]</c> list.</summary>
    public static IReadOnlyList<Thumbnail> From(JsonNode? list)
    {
        if (list is not JsonArray array || array.Count == 0)
        {
            return [];
        }

        var result = new List<Thumbnail>(array.Count);
        foreach (var entry in array)
        {
            if (entry.Str("url") is not { Length: > 0 } url)
            {
                continue;
            }

            // Channel avatars are sometimes protocol-relative ("//yt3.ggpht.com/...").
            if (url.StartsWith("//", StringComparison.Ordinal))
            {
                url = "https:" + url;
            }

            result.Add(new Thumbnail(url, (int)(entry.Long("width") ?? 0), (int)(entry.Long("height") ?? 0)));
        }

        return result;
    }

    /// <summary>List items and page headers: <c>.thumbnail.musicThumbnailRenderer.thumbnail.thumbnails</c> (falls back to the cropped variant).</summary>
    public static IReadOnlyList<Thumbnail> OfResponsive(JsonNode? item)
    {
        var thumbnails = From(item.Nav("thumbnail", "musicThumbnailRenderer", "thumbnail", "thumbnails"));
        return thumbnails.Count > 0
            ? thumbnails
            : From(item.Nav("thumbnail", "croppedSquareThumbnailRenderer", "thumbnail", "thumbnails"));
    }

    /// <summary>Carousel cards (musicTwoRowItemRenderer): <c>.thumbnailRenderer.musicThumbnailRenderer.thumbnail.thumbnails</c>.</summary>
    public static IReadOnlyList<Thumbnail> OfTwoRow(JsonNode? item) =>
        From(item.Nav("thumbnailRenderer", "musicThumbnailRenderer", "thumbnail", "thumbnails"));

    /// <summary>Watch queue rows: <c>.thumbnail.thumbnails</c>.</summary>
    public static IReadOnlyList<Thumbnail> OfPlain(JsonNode? item) => From(item.Nav("thumbnail", "thumbnails"));
}
