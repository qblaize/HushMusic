using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Carousel/grid cards (<c>musicTwoRowItemRenderer</c>) on home, artist, album and library pages.
/// Dispatch follows ytmusicapi parse_mixed_content: by the title link's pageType, else by the card's own endpoint.
/// </summary>
internal static partial class TwoRowItemParser
{
    public const string RendererName = "musicTwoRowItemRenderer";

    public static MediaItem? Parse(JsonObject card, ParseScope scope)
    {
        var titleRun = card.Obj("title", "runs", 0);
        var cardEndpoint = card.Obj("navigationEndpoint");
        var pageType = PageTypes.Of(titleRun.Obj("navigationEndpoint")) ?? PageTypes.Of(cardEndpoint);
        var kind = pageType switch
        {
            null => BrowseIds.KindOf(TextRuns.BrowseId(titleRun) ?? cardEndpoint.Str("browseEndpoint", "browseId")),
            _ when PageTypes.IsAlbum(pageType) => SearchTypes.Album,
            _ when PageTypes.IsArtist(pageType) => SearchTypes.Artist,
            PageTypes.Playlist => SearchTypes.Playlist,
            PageTypes.PodcastShow => SearchTypes.Podcast,
            _ => "unknown",
        };

        switch (kind)
        {
            case SearchTypes.Album:
                return ParseAlbum(card, scope);
            case SearchTypes.Artist:
                return ParseArtist(card, scope);
            case SearchTypes.Playlist:
                return ParsePlaylist(card, scope);
            case SearchTypes.Podcast:
                scope.IgnoreItem(RendererName, "podcasts are not modelled");
                return null;
            case "unknown":
                scope.SkipItem(RendererName, $"unsupported pageType {pageType}");
                return null;
        }

        // No browse link on the title: a song/video card or a mix ("watch playlist") card.
        if (card.Str("navigationEndpoint", "watchPlaylistEndpoint", "playlistId") is { } watchPlaylistId)
        {
            return ParseWatchPlaylist(card, watchPlaylistId, scope);
        }

        if (card.Str("navigationEndpoint", "watchEndpoint", "videoId") is not null || QueueVideoId(card) is not null)
        {
            return ParseSong(card, scope);
        }

        // ytmusicapi: deleted uploads can still appear in "Listen again" without any destination.
        scope.IgnoreItem(RendererName, "card has neither a browse nor a watch endpoint");
        return null;
    }

    /// <summary>Album / single / EP card (ytmusicapi parse_album, parse_single, parse_albums).</summary>
    public static Album? ParseAlbum(JsonObject card, ParseScope scope)
    {
        var titleRun = card.Obj("title", "runs", 0);
        var title = titleRun.Str("text");
        var browseId = TextRuns.BrowseId(titleRun) ?? card.Str("navigationEndpoint", "browseEndpoint", "browseId");
        if (title is null || browseId is null)
        {
            scope.SkipItem(RendererName, "album card without title or browseId");
            return null;
        }

        var runs = card.Arr("subtitle", "runs");
        string? typeText = null, year = null;
        IReadOnlyList<ArtistRef> artists = [];
        if (runs is { Count: > 0 })
        {
            var first = runs[0];
            var firstText = first.Str("text") ?? string.Empty;
            if (!TextRuns.HasLink(first) && firstText.Length > 0 && firstText.All(char.IsAsciiDigit))
            {
                // Artist page album carousels show only the year.
                year = firstText;
            }
            else
            {
                // "Single • Artist • 2023": a leading unlinked word is the release type.
                var skip = TextRuns.HasLink(first) ? 0 : 2;
                typeText = skip == 2 ? firstText : null;
                var info = TextRuns.ParseSongRuns(runs, skip);
                year = info.Year;
                artists = info.Artists;
            }

            // ytmusicapi parse_album only trusts linked runs for artists when there are any.
            var linked = runs.Where(TextRuns.HasLink)
                .Where(r => !BrowseIds.IsAlbum(TextRuns.BrowseId(r)))
                .Select(r => new ArtistRef(r.Str("text") ?? string.Empty, TextRuns.BrowseId(r)))
                .Where(a => a.Name.Length > 0)
                .ToList();
            if (linked.Count > 0)
            {
                artists = linked;
            }
        }

        var play = card.Obj("thumbnailOverlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint");
        return new Album
        {
            Title = title,
            BrowseId = browseId,
            Thumbnails = Thumbnails.OfTwoRow(card),
            Artists = artists,
            Year = year,
            Type = EnglishText.ParseAlbumType(typeText) ?? AlbumType.Album,
            IsExplicit = HasSubtitleBadge(card),
            AudioPlaylistId = play.Str("watchPlaylistEndpoint", "playlistId")
                ?? play.Str("watchEndpoint", "playlistId")
                ?? Menus.Items(card).Str(0, "menuNavigationItemRenderer", "navigationEndpoint", "watchPlaylistEndpoint", "playlistId"),
        };
    }

    /// <summary>Artist / channel card (ytmusicapi parse_related_artist).</summary>
    public static Artist? ParseArtist(JsonObject card, ParseScope scope)
    {
        var titleRun = card.Obj("title", "runs", 0);
        var title = titleRun.Str("text");
        var browseId = TextRuns.BrowseId(titleRun) ?? card.Str("navigationEndpoint", "browseEndpoint", "browseId");
        if (title is null || browseId is null)
        {
            scope.SkipItem(RendererName, "artist card without title or browseId");
            return null;
        }

        return new Artist
        {
            Title = title,
            BrowseId = browseId,
            Thumbnails = Thumbnails.OfTwoRow(card),
            // "7.19M subscribers" / "75.9M monthly audience" -> first token, as ytmusicapi does.
            Subscribers = TextRuns.FirstToken(card.Str("subtitle", "runs", 0, "text")),
        };
    }

    /// <summary>Playlist card (ytmusicapi parse_playlist).</summary>
    public static Playlist? ParsePlaylist(JsonObject card, ParseScope scope)
    {
        var titleRun = card.Obj("title", "runs", 0);
        var browseId = TextRuns.BrowseId(titleRun) ?? card.Str("navigationEndpoint", "browseEndpoint", "browseId");
        if (browseId is null)
        {
            scope.SkipItem(RendererName, "playlist card without browseId");
            return null;
        }

        var runs = card.Arr("subtitle", "runs");
        ArtistRef? author = null;
        int? trackCount = null;
        if (runs is { Count: 3 } && runs[2].Str("text") is { } countText && SongCountRegex().IsMatch(countText))
        {
            // "Author • 50 songs"
            trackCount = TextRuns.ParseInt(countText.Split(' ')[0]);
            author = TextRuns.ParseArtistsRuns(runs.Take(1)).FirstOrDefault();
        }
        else if (runs?.FirstOrDefault(r => TextRuns.BrowseId(r) is not null) is { } linked && linked.Str("text") is { } name)
        {
            // "Playlist • Daft Punk • 20M views": ytmusicapi leaves author empty here; the linked run is the owner.
            author = new ArtistRef(name, TextRuns.BrowseId(linked));
        }

        return new Playlist
        {
            // ytmusicapi tolerates a missing title on playlist cards ("rare but possible").
            Title = titleRun.Str("text") ?? string.Empty,
            PlaylistId = BrowseIds.StripVl(browseId),
            Thumbnails = Thumbnails.OfTwoRow(card),
            Description = TextRuns.Text(card.Obj("subtitle")),
            Author = author,
            TrackCount = trackCount,
        };
    }

    /// <summary>Mix card without a browse page; it is played through the watch endpoint (ytmusicapi parse_watch_playlist).</summary>
    private static Playlist? ParseWatchPlaylist(JsonObject card, string playlistId, ParseScope scope)
    {
        if (card.Str("title", "runs", 0, "text") is not { } title)
        {
            scope.SkipItem(RendererName, "watch playlist card without title");
            return null;
        }

        return new Playlist
        {
            Title = title,
            PlaylistId = playlistId,
            Thumbnails = Thumbnails.OfTwoRow(card),
            Description = TextRuns.Text(card.Obj("subtitle")),
            IsMix = true,
        };
    }

    /// <summary>Song or video card (ytmusicapi parse_song / parse_video).</summary>
    private static Track? ParseSong(JsonObject card, ParseScope scope)
    {
        var title = card.Str("title", "runs", 0, "text");
        var videoId = card.Str("navigationEndpoint", "watchEndpoint", "videoId") ?? QueueVideoId(card);
        if (title is null || videoId is null)
        {
            scope.SkipItem(RendererName, "song card without title or videoId");
            return null;
        }

        var info = TextRuns.ParseSongRuns(card.Arr("subtitle", "runs"), skipTypeSpec: true);
        return new Track
        {
            Title = title,
            VideoId = videoId,
            PlaylistId = card.Str("navigationEndpoint", "watchEndpoint", "playlistId"),
            Thumbnails = Thumbnails.OfTwoRow(card),
            Type = VideoTypes.ToTrackType(VideoTypes.Of(card.Obj("navigationEndpoint")), TrackType.Song),
            Artists = info.Artists,
            Album = info.Album,
            Views = info.Views,
            Duration = info.Duration,
            IsExplicit = HasSubtitleBadge(card),
        };
    }

    /// <summary>Video cards without a watchEndpoint still have the id in their "Add to queue" menu item.</summary>
    private static string? QueueVideoId(JsonObject card)
    {
        foreach (var entry in Menus.Items(card).Objects())
        {
            if (entry.Str("menuServiceItemRenderer", "serviceEndpoint", "queueAddEndpoint", "queueTarget", "videoId") is { } videoId)
            {
                return videoId;
            }
        }

        return null;
    }

    private static bool HasSubtitleBadge(JsonObject card) =>
        card.Str("subtitleBadges", 0, "musicInlineBadgeRenderer", "accessibilityData", "accessibilityData", "label") is not null;

    [GeneratedRegex(@"\d+ ")]
    private static partial Regex SongCountRegex();
}
