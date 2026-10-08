using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>The Explore tab (ytmusicapi <c>mixins/explore.py</c> and <c>mixins/charts.py</c>). Nothing here needs an account.</summary>
internal sealed class ExploreApi(IInnerTubeClient client, ILogger<ExploreApi> logger) : IExploreApi
{
    public async Task<ExplorePage> GetExploreAsync(CancellationToken cancellationToken = default) =>
        ExploreParser.Parse(await BrowseAsync(new JsonObject { ["browseId"] = "FEmusic_explore" }, cancellationToken).ConfigureAwait(false), logger);

    public async Task<IReadOnlyList<MoodCategoryGroup>> GetMoodCategoriesAsync(CancellationToken cancellationToken = default) =>
        ExploreParser.ParseMoodCategories(
            await BrowseAsync(new JsonObject { ["browseId"] = "FEmusic_moods_and_genres" }, cancellationToken).ConfigureAwait(false),
            logger);

    public async Task<MoodPage> GetMoodPlaylistsAsync(string categoryParams, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryParams);

        var body = new JsonObject { ["browseId"] = "FEmusic_moods_and_genres_category", ["params"] = categoryParams };
        return ExploreParser.ParseMoodPlaylists(await BrowseAsync(body, cancellationToken).ConfigureAwait(false), logger);
    }

    public async Task<ChartsPage> GetChartsAsync(string? country = "ZZ", CancellationToken cancellationToken = default)
    {
        // Same as ytmusicapi get_charts: the country goes in formData, which only an explicit country adds.
        var body = new JsonObject { ["browseId"] = "FEmusic_charts" };
        if (!string.IsNullOrWhiteSpace(country))
        {
            body["formData"] = new JsonObject { ["selectedValues"] = new JsonArray(country.Trim().ToUpperInvariant()) };
        }

        return ChartsParser.Parse(await BrowseAsync(body, cancellationToken).ConfigureAwait(false), logger);
    }

    // ytmusicapi has no call for the two full new-release lists. They are the browse ids behind the Explore page's own
    // "More" buttons ("New albums & singles", "New music videos"), and each returns one grid of the usual cards.
    public async Task<IReadOnlyList<Album>> GetNewReleasesAsync(CancellationToken cancellationToken = default) =>
        ExploreParser.ParseNewReleases(
            await BrowseAsync(new JsonObject { ["browseId"] = "FEmusic_new_releases_albums" }, cancellationToken).ConfigureAwait(false),
            logger);

    public async Task<IReadOnlyList<Track>> GetNewVideosAsync(CancellationToken cancellationToken = default) =>
        ExploreParser.ParseNewVideos(
            await BrowseAsync(new JsonObject { ["browseId"] = "FEmusic_new_releases_videos" }, cancellationToken).ConfigureAwait(false),
            logger);

    private Task<JsonNode> BrowseAsync(JsonObject body, CancellationToken cancellationToken) =>
        client.PostAsync(new InnerTubeRequest("browse", body), cancellationToken);
}
