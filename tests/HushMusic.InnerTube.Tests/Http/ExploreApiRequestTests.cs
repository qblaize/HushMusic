using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.InnerTube.Api;
using HushMusic.InnerTube.Tests.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>Request shapes of the Explore calls and the artist discography. Golden bodies are the ones ytmusicapi sent (Fixtures/EXPECTED.md).</summary>
public sealed class ExploreApiRequestTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ExploreApi Explore => new(_host.Client, NullLogger<ExploreApi>.Instance);

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Explore_browses_FEmusic_explore()
    {
        _host.Handler.EnqueueFixture("explore.json");

        var page = await Explore.GetExploreAsync(Ct);

        Assert.Equal("""{"browseId":"FEmusic_explore"}""", _host.Handler.Last.BodyWithoutContext);
        Assert.Equal("/youtubei/v1/browse", _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal(24, page.NewReleases.Count);
    }

    [Fact]
    public async Task Mood_categories_and_playlists()
    {
        await Explore.GetMoodCategoriesAsync(Ct);
        Assert.Equal("""{"browseId":"FEmusic_moods_and_genres"}""", _host.Handler.Last.BodyWithoutContext);

        await Explore.GetMoodPlaylistsAsync("ggMPOg1uX3NmUVV4Vzl3WGQ0", Ct);
        Assert.Equal("""{"browseId":"FEmusic_moods_and_genres_category","params":"ggMPOg1uX3NmUVV4Vzl3WGQ0"}""", _host.Handler.Last.BodyWithoutContext);

        await Assert.ThrowsAsync<ArgumentException>(() => Explore.GetMoodPlaylistsAsync(" ", Ct));
        Assert.Equal(2, _host.Handler.Requests.Count);
    }

    [Theory]
    [InlineData("US", """{"browseId":"FEmusic_charts","formData":{"selectedValues":["US"]}}""")]
    [InlineData(" ro ", """{"browseId":"FEmusic_charts","formData":{"selectedValues":["RO"]}}""")]
    [InlineData(null, """{"browseId":"FEmusic_charts"}""")]
    public async Task Charts_send_the_country_as_form_data(string? country, string expectedBody)
    {
        await Explore.GetChartsAsync(country, Ct);

        Assert.Equal(expectedBody, _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Charts_default_to_global_like_ytmusicapi()
    {
        await Explore.GetChartsAsync(cancellationToken: Ct);

        Assert.Equal("""{"browseId":"FEmusic_charts","formData":{"selectedValues":["ZZ"]}}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task New_release_lists_follow_the_explore_page_more_links()
    {
        await Explore.GetNewReleasesAsync(Ct);
        Assert.Equal("""{"browseId":"FEmusic_new_releases_albums"}""", _host.Handler.Last.BodyWithoutContext);

        await Explore.GetNewVideosAsync(Ct);
        Assert.Equal("""{"browseId":"FEmusic_new_releases_videos"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Explore_works_signed_out()
    {
        _host.Handler.EnqueueFixture("charts_us.json");

        var charts = await Explore.GetChartsAsync("US", Ct);

        Assert.Equal("United States", charts.CountryName);
        Assert.Null(_host.Handler.Last.Header("Authorization"));
    }

    [Fact]
    public async Task Artist_albums_page_through_the_grid_with_ctoken()
    {
        _host.Handler.EnqueueFixture("artist_albums_paged.json");
        _host.Handler.EnqueueFixture("artist_albums_continuation.json");

        var first = await _host.Browse.GetArtistAlbumsAsync("MPADUCuA3IbLtd-mMVPH5gm16tWQ", "ggMIegYIARoCAQI%3D", null, Ct);
        Assert.Equal("""{"browseId":"MPADUCuA3IbLtd-mMVPH5gm16tWQ","params":"ggMIegYIARoCAQI%3D"}""", _host.Handler.Last.BodyWithoutContext);
        Assert.Equal(12, first.Items.Count);
        Assert.NotNull(first.Continuation);

        var rawToken = ParserFixtures.Load("artist_albums_paged.json")
            .At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0,
                "gridRenderer", "continuations", 0, "nextContinuationData", "continuation")
            .GetValue<string>();

        var next = await _host.Browse.GetArtistAlbumsAsync("", null, first.Continuation, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("""{"browseId":"MPADUCuA3IbLtd-mMVPH5gm16tWQ","params":"ggMIegYIARoCAQI%3D"}""", request.BodyWithoutContext);
        Assert.EndsWith($"&ctoken={rawToken}&continuation={rawToken}", request.Uri.OriginalString);
        Assert.Equal(10, next.Items.Count);
        Assert.Null(next.Continuation);
    }

    [Fact]
    public async Task Artist_albums_need_a_browse_id()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Browse.GetArtistAlbumsAsync(" ", "p", null, Ct));
        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Artist_albums_token_is_not_accepted_elsewhere()
    {
        var token = ContinuationToken.Wrap(ContinuationScope.ArtistAlbums, "abc", new JsonObject { ["browseId"] = "MPADx" })!;

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Browse.GetHomeAsync(token, Ct));
        Assert.Empty(_host.Handler.Requests);
    }
}
