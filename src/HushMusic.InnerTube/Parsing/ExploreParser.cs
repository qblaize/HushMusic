using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// The Explore tab: <c>FEmusic_explore</c> (ytmusicapi get_explore), <c>FEmusic_moods_and_genres</c>
/// (get_mood_categories), <c>FEmusic_moods_and_genres_category</c> (get_mood_playlists) and the full new-release
/// grids behind the Explore page's "More" links.
/// </summary>
internal static class ExploreParser
{
    private const string Page = "Explore";

    // get_explore recognises its carousels by the browse id behind their title, not by the (localised) title text.
    private const string NewReleasesId = "FEmusic_new_releases_albums";
    private const string MoodsAndGenresId = "FEmusic_moods_and_genres";
    private const string NewVideosId = "FEmusic_new_releases_videos";
    private const string TopEpisodesId = "FEmusic_top_non_music_audio_episodes";

    public static ExplorePage Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        if (SectionList(response) is not { } sections)
        {
            scope.MissingStructure("contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents");
            return new ExplorePage();
        }

        var page = new ExplorePage();
        foreach (var section in sections.Objects())
        {
            var carousel = section.Obj("musicCarouselShelfRenderer");
            var titleRun = carousel.Obj("header", "musicCarouselShelfBasicHeaderRenderer", "title", "runs", 0);

            // The row of New releases / Charts / Moods & genres buttons at the top has no titled carousel.
            if (TextRuns.BrowseId(titleRun) is not { } browseId)
            {
                continue;
            }

            var contents = carousel.Arr("contents");
            switch (browseId)
            {
                case NewReleasesId:
                    page = page with { NewReleases = ParseCards(contents, TwoRowItemParser.ParseAlbum, scope) };
                    break;
                case MoodsAndGenresId:
                    page = page with { MoodsAndGenres = ParseMoodButtons(contents, scope) };
                    break;
                case NewVideosId:
                    page = page with { NewVideos = ParseCards(contents, TwoRowItemParser.ParseSong, scope) };
                    break;
                case TopEpisodesId:
                    scope.IgnoreItem("musicCarouselShelfRenderer", "podcast episodes are not shown");
                    break;

                // Chart playlists: "Top songs" is a PL... playlist, "Trending" an OLAK5uy_... one.
                case { } when browseId.StartsWith("VLPL", StringComparison.Ordinal):
                    page = page with { TopSongs = ParseSongChart(titleRun, browseId, contents, scope) };
                    break;
                case { } when browseId.StartsWith("VLOLA", StringComparison.Ordinal):
                    page = page with { Trending = ParseSongChart(titleRun, browseId, contents, scope) };
                    break;
                default:
                    scope.IgnoreItem("musicCarouselShelfRenderer", $"unknown explore section {browseId}");
                    break;
            }
        }

        return page;
    }

    /// <summary>
    /// <c>FEmusic_moods_and_genres</c>: one grid of category buttons per group ("Moods &amp; moments", "Genres").
    /// </summary>
    public static IReadOnlyList<MoodCategoryGroup> ParseMoodCategories(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, "MoodCategories");
        if (SectionList(response) is not { } sections)
        {
            scope.MissingStructure("contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents");
            return [];
        }

        var groups = new List<MoodCategoryGroup>();
        foreach (var section in sections.Objects())
        {
            if (section.Obj("gridRenderer") is not { } grid)
            {
                scope.IgnoreItem(section.Describe(), "section is not a grid of categories");
                continue;
            }

            var title = grid.Str("header", "gridHeaderRenderer", "title", "runs", 0, "text") ?? string.Empty;
            groups.Add(new MoodCategoryGroup(title, ParseMoodButtons(grid.Arr("items"), scope)));
        }

        return groups;
    }

    /// <summary>
    /// <c>FEmusic_moods_and_genres_category</c> + params: the category's carousels and grids, in page order.
    /// </summary>
    /// <remarks>
    /// ytmusicapi get_mood_playlists flattens the page into one list and parses every card as a playlist, which raises on
    /// genre pages (their "Music videos" cards have no browse link) and turns album ids into playlist ids. Here each
    /// section stays a <see cref="Shelf"/> and every card is parsed by its own type, like the home feed.
    /// </remarks>
    public static MoodPage ParseMoodPlaylists(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, "MoodPlaylists");
        var title = response.Str("header", "musicHeaderRenderer", "title", "runs", 0, "text") ?? string.Empty;
        if (SectionList(response) is not { } sections)
        {
            scope.MissingStructure("contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents");
            return new MoodPage { Title = title };
        }

        var shelves = new List<Shelf>();
        foreach (var section in sections.Objects())
        {
            var (name, renderer) = section.Renderer();
            var contents = name switch
            {
                "gridRenderer" => renderer.Arr("items"),
                "musicCarouselShelfRenderer" or "musicImmersiveCarouselShelfRenderer" => renderer.Arr("contents"),
                _ => null,
            };

            if (renderer is null || contents is null)
            {
                scope.IgnoreItem(section.Describe(), "section without items");
                continue;
            }

            if (ShelfParser.ParseShelf(renderer, contents, scope) is { Items.Count: > 0 } shelf)
            {
                shelves.Add(shelf);
            }
        }

        return new MoodPage { Title = title, Sections = shelves };
    }

    /// <summary>
    /// <c>FEmusic_new_releases_albums</c>, the Explore page's "New albums &amp; singles" → More: one grid of album
    /// cards, parsed like an artist's discography grid (ytmusicapi parse_albums).
    /// </summary>
    public static IReadOnlyList<Album> ParseNewReleases(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, "NewReleases");
        return GridItems(response, scope) is { } items ? LibraryParser.ParseAlbumCards(items, scope) : [];
    }

    /// <summary><c>FEmusic_new_releases_videos</c>, "New music videos" → More: one grid of video cards (ytmusicapi parse_video).</summary>
    public static IReadOnlyList<Track> ParseNewVideos(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, "NewVideos");
        return GridItems(response, scope) is { } items
            ? ParseCards(items, TwoRowItemParser.ParseSong, scope)
            : [];
    }

    internal static JsonArray? SectionList(JsonNode response) =>
        response.Arr("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");

    /// <summary>Ranked rows of a chart carousel (ytmusicapi parse_chart_song / parse_trending_item, plus parse_ranking).</summary>
    private static SongChart ParseSongChart(JsonObject? titleRun, string browseId, JsonArray? contents, ParseScope scope)
    {
        var entries = new List<ChartEntry<Track>>();
        foreach (var entry in contents.Objects())
        {
            if (entry.Obj("musicResponsiveListItemRenderer") is not { } row)
            {
                scope.IgnoreItem(entry.Describe(), "chart item is not a song row");
                continue;
            }

            if (ShelfParser.ParseFlatSong(row, scope) is { } track)
            {
                var (rank, trend) = ChartRanking.Of(row);
                entries.Add(new ChartEntry<Track>(track, rank, trend));
            }
        }

        return new SongChart
        {
            Title = titleRun.Str("text") ?? string.Empty,
            PlaylistId = BrowseIds.StripVl(browseId),
            Entries = entries,
        };
    }

    /// <summary>
    /// Category buttons (ytmusicapi: <c>musicNavigationButtonRenderer</c> title and <c>clickCommand.browseEndpoint.params</c>).
    /// The stripe colour is not read by ytmusicapi; YouTube Music paints the button with it.
    /// </summary>
    private static List<MoodCategory> ParseMoodButtons(JsonArray? items, ParseScope scope)
    {
        var categories = new List<MoodCategory>();
        foreach (var entry in items.Objects())
        {
            var button = entry.Obj("musicNavigationButtonRenderer");
            var title = button.Str("buttonText", "runs", 0, "text");
            var categoryParams = button.Str("clickCommand", "browseEndpoint", "params");
            if (title is null || categoryParams is null)
            {
                scope.SkipItem(entry.Describe(), "category button without title or params");
                continue;
            }

            var color = button.Long("solid", "leftStripeColor");
            categories.Add(new MoodCategory(title, categoryParams)
            {
                Color = color is >= 0 and <= 0xFFFFFFFFL ? (uint)color.Value : null,
            });
        }

        return categories;
    }

    /// <summary>ytmusicapi parse_content_list: only <c>musicTwoRowItemRenderer</c> entries count, others are passed over.</summary>
    private static List<T> ParseCards<T>(JsonArray? contents, Func<JsonObject, ParseScope, T?> parse, ParseScope scope)
        where T : class
    {
        var items = new List<T>();
        foreach (var entry in contents.Objects())
        {
            if (entry.Obj(TwoRowItemParser.RendererName) is not { } card)
            {
                scope.IgnoreItem(entry.Describe(), "not a card");
                continue;
            }

            if (parse(card, scope) is { } item)
            {
                items.Add(item);
            }
        }

        return items;
    }

    private static JsonArray? GridItems(JsonNode response, ParseScope scope)
    {
        var items = SectionList(response).Obj(0)?.Obj("gridRenderer")?.Arr("items");
        if (items is null)
        {
            scope.MissingStructure("sectionListRenderer.contents[0].gridRenderer.items");
        }

        return items;
    }
}

/// <summary>
/// A chart row's position and movement (ytmusicapi parse_ranking):
/// <c>customIndexColumn.musicCustomIndexColumnRenderer</c> text and icon.
/// </summary>
internal static class ChartRanking
{
    public static (int? Rank, ChartTrend? Trend) Of(JsonObject row)
    {
        var column = row.Obj("customIndexColumn", "musicCustomIndexColumnRenderer");
        var trend = column.Str("icon", "iconType") switch
        {
            "ARROW_DROP_UP" => ChartTrend.Up,
            "ARROW_DROP_DOWN" => ChartTrend.Down,
            "ARROW_CHART_NEUTRAL" => ChartTrend.Neutral,
            _ => (ChartTrend?)null,
        };

        return (TextRuns.ParseInt(column.Str("text", "runs", 0, "text")), trend);
    }
}
