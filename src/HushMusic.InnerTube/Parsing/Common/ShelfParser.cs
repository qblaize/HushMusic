using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Section-list rows made of carousels (home feed, artist page sections, album "other versions").
/// Port of ytmusicapi parse_mixed_content, extended to keep each shelf's "more" link.
/// </summary>
internal static class ShelfParser
{
    /// <summary>Parses every row of a section list; rows that are not item shelves are skipped.</summary>
    public static List<Shelf> ParseRows(JsonArray? rows, ParseScope scope)
    {
        var shelves = new List<Shelf>();
        foreach (var row in rows.Objects())
        {
            if (ParseRow(row, scope) is { } shelf)
            {
                shelves.Add(shelf);
            }
        }

        return shelves;
    }

    public static Shelf? ParseRow(JsonObject row, ParseScope scope)
    {
        var (name, renderer) = row.Renderer();
        if (name is null || renderer is null)
        {
            scope.SkipItem(row.Describe(), "section row is not a renderer object");
            return null;
        }

        if (name == "musicDescriptionShelfRenderer")
        {
            // A text block ({title, description}); it has no items.
            return new Shelf
            {
                Title = TextRuns.Text(renderer.Obj("header")) ?? string.Empty,
                Subtitle = TextRuns.Text(renderer.Obj("description")),
            };
        }

        if (renderer.Arr("contents") is not { } contents)
        {
            // e.g. musicTastebuilderShelfRenderer on the home page.
            scope.IgnoreItem(name, "section without contents");
            return null;
        }

        return ParseShelf(renderer, contents, scope);
    }

    /// <summary>A carousel (<c>musicCarouselShelfRenderer</c>, <c>musicImmersiveCarouselShelfRenderer</c>...).</summary>
    public static Shelf ParseShelf(JsonObject renderer, JsonArray contents, ParseScope scope)
    {
        // The header renderer's name differs per carousel type; its title/strapline layout does not.
        var (_, header) = renderer.Obj("header").Renderer();
        var titleRun = header.Obj("title", "runs", 0);
        var title = TextRuns.Text(header.Obj("title"));
        var strapline = TextRuns.Text(header.Obj("strapline"));
        var more = titleRun.Obj("navigationEndpoint", "browseEndpoint")
            ?? header.Obj("moreContentButton", "buttonRenderer", "navigationEndpoint", "browseEndpoint");

        return new Shelf
        {
            // Some carousels only carry a strapline ("MORE FROM ...") instead of a title.
            Title = title ?? strapline ?? string.Empty,
            Subtitle = title is null ? null : strapline,
            Items = ParseItems(contents, scope),
            MoreBrowseId = more.Str("browseId"),
            MoreParams = more.Str("params"),
            Layout = LayoutOf(contents),
        };
    }

    /// <summary>
    /// Carousels of <c>musicResponsiveListItemRenderer</c> rows ("Quick picks", "You might also like") are shown by
    /// YouTube Music as a grid of compact song rows; everything else as cards. The first item decides.
    /// </summary>
    public static ShelfLayout LayoutOf(JsonArray? contents) =>
        contents.Objects().Select(entry => entry.Renderer().Name).FirstOrDefault(name => name is not null) == "musicResponsiveListItemRenderer"
            ? ShelfLayout.List
            : ShelfLayout.Cards;

    /// <summary>Items of a carousel: cards, flat song rows (Quick picks) and episode rows.</summary>
    public static List<MediaItem> ParseItems(JsonArray? contents, ParseScope scope)
    {
        var items = new List<MediaItem>();
        foreach (var entry in contents.Objects())
        {
            var (name, item) = entry.Renderer();
            MediaItem? parsed = name switch
            {
                TwoRowItemParser.RendererName when item is not null => TwoRowItemParser.Parse(item, scope),
                "musicResponsiveListItemRenderer" when item is not null => ParseFlatSong(item, scope),
                "musicMultiRowListItemRenderer" when item is not null => ParseEpisode(item, scope),
                _ => Unsupported(entry, scope),
            };

            if (parsed is not null)
            {
                items.Add(parsed);
            }
        }

        return items;
    }

    /// <summary>Song row in a carousel, e.g. "Quick picks" (ytmusicapi parse_song_flat).</summary>
    public static Track? ParseFlatSong(JsonObject row, ParseScope scope)
    {
        const string renderer = "musicResponsiveListItemRenderer";
        var titleRun = Columns.FlexRun(row, 0);
        var play = row.Obj("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint");

        // Podcast episodes link their title to the episode page, so the playable id comes from the play button.
        var videoId = titleRun.Str("navigationEndpoint", "watchEndpoint", "videoId") ?? play.Str("watchEndpoint", "videoId");
        var title = titleRun.Str("text");
        if (title is null || videoId is null)
        {
            scope.SkipItem(renderer, "song row without title or videoId");
            return null;
        }

        var info = TextRuns.ParseSongRuns(Columns.FlexRuns(row, 1), skipTypeSpec: true);
        var albumRun = Columns.FlexRun(row, 2);
        var album = TextRuns.HasLink(albumRun) && albumRun.Str("text") is { } albumName
            ? new AlbumRef(albumName, TextRuns.BrowseId(albumRun))
            : info.Album;

        return new Track
        {
            Title = title,
            VideoId = videoId,
            PlaylistId = play.Str("watchEndpoint", "playlistId"),
            Thumbnails = Thumbnails.OfResponsive(row),
            Type = VideoTypes.ToTrackType(VideoTypes.Of(play), TrackType.Song),
            Artists = info.Artists,
            Album = album,
            Views = info.Views,
            Duration = info.Duration,
            IsExplicit = PlaylistItemParser.HasBadge(row),
            IsAvailable = PlaylistItemParser.IsAvailable(row),
        };
    }

    /// <summary>Podcast episode row (ytmusicapi parse_episode).</summary>
    private static Track? ParseEpisode(JsonObject row, ParseScope scope)
    {
        var title = row.Str("title", "runs", 0, "text");
        var videoId = row.Str("onTap", "watchEndpoint", "videoId");
        if (title is null || videoId is null)
        {
            scope.SkipItem("musicMultiRowListItemRenderer", "episode row without title or videoId");
            return null;
        }

        return new Track
        {
            Title = title,
            VideoId = videoId,
            Type = TrackType.Episode,
            Thumbnails = Thumbnails.OfResponsive(row),
        };
    }

    private static MediaItem? Unsupported(JsonObject entry, ParseScope scope)
    {
        scope.SkipItem(entry.Describe(), "unsupported carousel item renderer");
        return null;
    }
}
