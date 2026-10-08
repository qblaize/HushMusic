using System.Text.Json.Nodes;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>
/// Search result rows and the search top-result card (ytmusicapi parse_search_result / parse_top_result).
/// Also used for the entity rows of search suggestions.
/// </summary>
internal static class SearchItemParser
{
    private const string RowRenderer = "musicResponsiveListItemRenderer";
    private const string CardRenderer = "musicCardShelfRenderer";

    /// <summary>
    /// One result row. <paramref name="resultType"/> is the filter's type (<see cref="SearchTypes"/>) or null
    /// for unfiltered results, in which case the type comes from the browseId prefix or the musicVideoType.
    /// </summary>
    public static MediaItem? ParseRow(JsonObject row, string? resultType, ParseScope scope)
    {
        // Unfiltered and album rows start column 1 with the type word: "Album • Daft Punk • 2013".
        var defaultOffset = resultType is null or SearchTypes.Album ? 2 : 0;
        var play = row.Obj("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint");
        var videoType = VideoTypes.Of(play);
        var browseId = row.Str("navigationEndpoint", "browseEndpoint", "browseId");

        if (resultType is null)
        {
            resultType = browseId is not null
                ? BrowseIds.KindOf(browseId)
                : videoType switch
                {
                    VideoTypes.Atv => SearchTypes.Song,
                    VideoTypes.PodcastEpisode => SearchTypes.Episode,
                    _ => SearchTypes.Video,
                };
        }

        var title = Columns.FlexText(row, 0);
        if (title is null)
        {
            scope.SkipItem(RowRenderer, $"{resultType ?? "unknown"} row without a title column");
            return null;
        }

        switch (resultType)
        {
            case SearchTypes.Artist or SearchTypes.Profile when browseId is not null:
                // Profiles (user channels, "UC..." ids) are reported as artists, as in ytmusicapi.
                return new Artist { Title = title, BrowseId = browseId, Thumbnails = Thumbnails.OfResponsive(row) };

            case SearchTypes.Album when browseId is not null:
            {
                var info = SongInfo(row);
                return new Album
                {
                    Title = title,
                    BrowseId = browseId,
                    Thumbnails = Thumbnails.OfResponsive(row),
                    Type = EnglishText.ParseAlbumType(Columns.FlexText(row, 1)) ?? AlbumType.Album,
                    Artists = info.Artists,
                    Year = info.Year,
                    IsExplicit = PlaylistItemParser.HasBadge(row),
                    AudioPlaylistId = play.Str("watchPlaylistEndpoint", "playlistId") ?? play.Str("watchEndpoint", "playlistId"),
                };
            }

            case SearchTypes.Playlist when browseId is not null:
                return ParsePlaylistRow(row, title, browseId, defaultOffset);

            case SearchTypes.Station when row.Str("navigationEndpoint", "watchEndpoint", "playlistId") is { } stationId:
                return new Playlist { Title = title, PlaylistId = stationId, Thumbnails = Thumbnails.OfResponsive(row), IsMix = true };

            case SearchTypes.Song or SearchTypes.Video or SearchTypes.Episode:
                return ParseTrackRow(row, title, resultType, play, videoType, defaultOffset, scope);

            case SearchTypes.Podcast:
                scope.IgnoreItem(RowRenderer, "podcasts are not modelled");
                return null;

            default:
                scope.SkipItem(RowRenderer, $"{resultType ?? "unrecognized"} row \"{title}\" without the id it needs");
                return null;
        }
    }

    /// <summary>The top-result card. Its kind is read from its endpoints first, the English type word second.</summary>
    public static MediaItem? ParseTopResult(JsonObject card, ParseScope scope)
    {
        var titleRun = card.Obj("title", "runs", 0);
        var title = titleRun.Str("text");
        var subtitleRuns = card.Arr("subtitle", "runs");
        if (title is null)
        {
            scope.SkipItem(CardRenderer, "top result without title");
            return null;
        }

        var onTap = card.Obj("onTap");
        var browseId = TextRuns.BrowseId(titleRun) ?? onTap.Str("browseEndpoint", "browseId");
        var pageType = PageTypes.Of(titleRun.Obj("navigationEndpoint")) ?? PageTypes.Of(onTap);
        var watchVideoId = onTap.Str("watchEndpoint", "videoId");
        var videoType = VideoTypes.Of(onTap);

        var kind = (watchVideoId, pageType) switch
        {
            ({ }, _) => videoType switch
            {
                VideoTypes.Atv => SearchTypes.Song,
                VideoTypes.PodcastEpisode => SearchTypes.Episode,
                null => EnglishText.TopResultType(subtitleRuns.Str(0, "text")),
                _ => SearchTypes.Video,
            },
            (_, { } p) when PageTypes.IsArtist(p) => SearchTypes.Artist,
            (_, { } p) when PageTypes.IsAlbum(p) => SearchTypes.Album,
            (_, PageTypes.Playlist) => SearchTypes.Playlist,
            _ => EnglishText.TopResultType(subtitleRuns.Str(0, "text")),
        };

        var thumbnails = Thumbnails.OfResponsive(card);
        switch (kind)
        {
            case SearchTypes.Artist or SearchTypes.Profile when browseId is not null:
                return new Artist
                {
                    Title = title,
                    BrowseId = browseId,
                    Thumbnails = thumbnails,
                    Subscribers = TextRuns.FirstToken(subtitleRuns.Str(2, "text")),
                };

            case SearchTypes.Song or SearchTypes.Video when watchVideoId is not null:
            {
                var info = TextRuns.ParseSongRuns(subtitleRuns, 2);
                return new Track
                {
                    Title = title,
                    VideoId = watchVideoId,
                    Type = VideoTypes.ToTrackType(videoType, kind == SearchTypes.Song ? TrackType.Song : TrackType.Video),
                    Thumbnails = thumbnails,
                    Artists = info.Artists,
                    Album = info.Album,
                    Views = info.Views,
                    Duration = info.Duration,
                    PlaylistId = onTap.Str("watchEndpoint", "playlistId"),
                };
            }

            case SearchTypes.Album when browseId is not null:
            {
                var info = TextRuns.ParseSongRuns(subtitleRuns, 2);
                var command = card.Obj("buttons", 0, "buttonRenderer", "command");
                return new Album
                {
                    Title = title,
                    BrowseId = browseId,
                    Thumbnails = thumbnails,
                    Type = EnglishText.ParseAlbumType(subtitleRuns.Str(0, "text")) ?? AlbumType.Album,
                    Artists = info.Artists,
                    Year = info.Year,
                    AudioPlaylistId = command.Str("watchPlaylistEndpoint", "playlistId") ?? command.Str("watchEndpoint", "playlistId"),
                };
            }

            case SearchTypes.Playlist:
            {
                var playlistId = Menus.Items(card).Str(0, "menuNavigationItemRenderer", "navigationEndpoint", "watchPlaylistEndpoint", "playlistId")
                    ?? (browseId is null ? null : BrowseIds.StripVl(browseId));
                if (playlistId is null)
                {
                    break;
                }

                return new Playlist
                {
                    Title = title,
                    PlaylistId = playlistId,
                    Thumbnails = thumbnails,
                    Author = TextRuns.ParseArtistsRuns(subtitleRuns?.Skip(2)).FirstOrDefault(),
                };
            }

            case SearchTypes.Episode:
            {
                var episodeVideoId = watchVideoId
                    ?? card.Str("thumbnailOverlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchEndpoint", "videoId");
                if (episodeVideoId is null)
                {
                    break;
                }

                return new Track
                {
                    Title = title,
                    VideoId = episodeVideoId,
                    Type = TrackType.Episode,
                    Thumbnails = thumbnails,
                    Artists = PodcastAsArtist(subtitleRuns.Obj(4)),
                };
            }

            case SearchTypes.Podcast:
                scope.IgnoreItem(CardRenderer, "podcasts are not modelled");
                return null;
        }

        scope.SkipItem(CardRenderer, $"{kind ?? "unrecognized"} top result \"{title}\" without the id it needs");
        return null;
    }

    private static Track? ParseTrackRow(
        JsonObject row, string title, string resultType, JsonObject? play, string? videoType, int defaultOffset, ParseScope scope)
    {
        if (play.Str("watchEndpoint", "videoId") is not { } videoId)
        {
            scope.SkipItem(RowRenderer, $"{resultType} row \"{title}\" without videoId");
            return null;
        }

        var fallbackType = resultType switch
        {
            SearchTypes.Song => TrackType.Song,
            SearchTypes.Episode => TrackType.Episode,
            _ => TrackType.Video,
        };

        if (resultType == SearchTypes.Episode)
        {
            // "Episode • Sep 8 • Podcast name": the podcast is shown where songs show the artist.
            var runs = Columns.FlexRuns(row, 1)?.Skip(defaultOffset).ToList() ?? [];
            var hasDate = runs.Count > 1 ? 1 : 0;
            return new Track
            {
                Title = title,
                VideoId = videoId,
                Type = VideoTypes.ToTrackType(videoType, fallbackType),
                Thumbnails = Thumbnails.OfResponsive(row),
                IsAvailable = PlaylistItemParser.IsAvailable(row),
                Artists = PodcastAsArtist(runs.ElementAtOrDefault(hasDate * 2)),
            };
        }

        var info = SongInfo(row);
        return new Track
        {
            Title = title,
            VideoId = videoId,
            Type = VideoTypes.ToTrackType(videoType, fallbackType),
            Thumbnails = Thumbnails.OfResponsive(row),
            IsAvailable = PlaylistItemParser.IsAvailable(row),
            IsExplicit = resultType == SearchTypes.Song && PlaylistItemParser.HasBadge(row),
            Artists = info.Artists,
            Album = info.Album,
            Views = info.Views,
            Duration = info.Duration,
            PlaylistId = play.Str("watchEndpoint", "playlistId"),
            FeedbackTokens = Menus.LibraryTokens(row),
        };
    }

    private static Playlist ParsePlaylistRow(JsonObject row, string title, string browseId, int defaultOffset)
    {
        var runs = Columns.FlexRuns(row, 1);
        var hasAuthor = runs?.Count == defaultOffset + 3;
        var author = hasAuthor ? runs.Obj(defaultOffset) : null;

        // ytmusicapi reads the count from run (has_author * 2), which ignores the type-word offset of
        // unfiltered rows ("Playlist • YouTube Music • 35 songs") and always yields None; the count is the
        // run after the author.
        var countText = runs.Str(defaultOffset + (hasAuthor ? 2 : 0), "text");
        return new Playlist
        {
            Title = title,
            PlaylistId = BrowseIds.StripVl(browseId),
            Thumbnails = Thumbnails.OfResponsive(row),
            Author = author.Str("text") is { } name ? new ArtistRef(name, TextRuns.BrowseId(author)) : null,
            TrackCount = EnglishText.ParseSongCount(countText),
        };
    }

    /// <summary>Flex column 1 + an empty separator + flex column 2, so the even/odd run parity is kept.</summary>
    private static SongRunsInfo SongInfo(JsonObject row)
    {
        var runs = new List<JsonNode?>();
        if (Columns.FlexRuns(row, 1) is { } first)
        {
            runs.AddRange(first);
        }

        if (Columns.FlexRuns(row, 2) is { } second)
        {
            runs.Add(new JsonObject { ["text"] = string.Empty });
            runs.AddRange(second);
        }

        return TextRuns.ParseSongRuns(runs, skipTypeSpec: true);
    }

    private static IReadOnlyList<ArtistRef> PodcastAsArtist(JsonNode? run) =>
        run.Str("text") is { Length: > 0 } name ? [new ArtistRef(name, null)] : [];
}
