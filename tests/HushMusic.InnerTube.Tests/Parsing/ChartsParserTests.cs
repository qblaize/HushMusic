using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class ChartsParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Country_menu()
    {
        var page = ChartsParser.Parse(ParserFixtures.Load("charts_us.json"), _log);

        Assert.Equal("United States", page.CountryName);
        Assert.Equal(69, page.Countries.Count);
        Assert.Equal(["RO", "ZZ", "AR"], page.Countries.Take(3));
        Assert.Equal("ZW", page.Countries[^1]);
        Assert.Contains("US", page.Countries);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Playlist_charts_keep_their_titles()
    {
        var page = ChartsParser.Parse(ParserFixtures.Load("charts_us.json"), _log);

        // ytmusicapi calls these "videos" and (US only) "genres".
        Assert.Equal(["Video charts", "Genres"], page.PlaylistCharts.Select(s => s.Title));
        Assert.Equal([4, 8], page.PlaylistCharts.Select(s => s.Items.Count));

        var first = Assert.IsType<Playlist>(page.PlaylistCharts[0].Items[0]);
        Assert.Equal(("Top 100 Live Performances - United States", "PL4fGSI1pDJn4yCNzulPkUbxgr4pl0gmI-"), (first.Title, first.PlaylistId));
        Assert.NotEmpty(first.Thumbnails);
        var lastVideos = Assert.IsType<Playlist>(page.PlaylistCharts[0].Items[^1]);
        Assert.Equal(("Top 100 Music Videos United States", "PL4fGSI1pDJn69On1f-8NAvX_CYlx7QyZc"), (lastVideos.Title, lastVideos.PlaylistId));

        var jazz = Assert.IsType<Playlist>(page.PlaylistCharts[1].Items[0]);
        Assert.Equal(("Top 50 Jazz Music Videos United States", "PL4fGSI1pDJn7Wkr6Ll6ds1AhA42rT8uaU"), (jazz.Title, jazz.PlaylistId));
    }

    [Fact]
    public void Top_artists_are_ranked()
    {
        var page = ChartsParser.Parse(ParserFixtures.Load("charts_us.json"), _log);

        Assert.Equal(40, page.TopArtists.Count);
        var drake = page.TopArtists[0];
        Assert.Equal(("Drake", "UCU6cE7pdJPc6DU2jSrKEsdQ", "33.2M"), (drake.Item.Title, drake.Item.BrowseId, drake.Item.Subscribers));
        Assert.Equal((1, ChartTrend.Neutral), (drake.Rank, drake.Trend));
        Assert.NotEmpty(drake.Item.Thumbnails);

        var last = page.TopArtists[^1];
        Assert.Equal(("2Pac", "UC5RrGzC-JXglhFW5NhT4r6w", "9.68M", 40), (last.Item.Title, last.Item.BrowseId, last.Item.Subscribers, last.Rank));
        Assert.Equal(15, page.TopArtists.Count(a => a.Trend == ChartTrend.Neutral));
        Assert.Equal(15, page.TopArtists.Count(a => a.Trend == ChartTrend.Down));
        Assert.Equal(10, page.TopArtists.Count(a => a.Trend == ChartTrend.Up));
    }

    [Fact]
    public void Podcast_shows_are_not_taken_for_artists()
    {
        // "Weekly top podcast shows" is also a carousel of list rows; only the first such carousel is the artist chart.
        var page = ChartsParser.Parse(ParserFixtures.Load("charts_us.json"), _log);

        Assert.DoesNotContain(page.TopArtists, a => a.Item.BrowseId.StartsWith("MPSP", StringComparison.Ordinal));
        Assert.Equal("Drake", page.TopArtists[0].Item.Title);
    }

    [Fact]
    public void Missing_country_menu_still_parses_the_charts()
    {
        var response = ParserFixtures.Load("charts_us.json");
        response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0)
            .Delete("subheaders", "musicShelfRenderer");

        var page = ChartsParser.Parse(response, _log);

        Assert.Null(page.CountryName);
        Assert.Equal(40, page.TopArtists.Count);
        Assert.Single(_log.Warnings);
    }
}
