using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

/// <summary>Every captured response goes through its parser without a single warning.</summary>
public sealed class FixtureCoverageTests
{
    public static TheoryData<string> Fixtures => new(ParserFixtures.RealFixtureNames());

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Real_fixture_parses_without_warnings(string fixture)
    {
        var log = new CapturingLogger();
        var parsed = Parse(fixture, ParserFixtures.Load(fixture), log);

        Assert.NotNull(parsed);
        Assert.Empty(log.Warnings);
    }

    private static object? Parse(string fixture, JsonNode response, ILogger log) => fixture switch
    {
        "home.json" => HomeParser.Parse(response, log),
        "home_continuation.json" => HomeParser.ParseContinuation(response, log),
        "search_all.json" => SearchParser.Parse(response, SearchFilter.All, log),
        "search_songs.json" => SearchParser.Parse(response, SearchFilter.Songs, log),
        "search_songs_continuation.json" => SearchParser.ParseContinuation(response, SearchFilter.Songs, log),
        "search_albums.json" => SearchParser.Parse(response, SearchFilter.Albums, log),
        "search_artists.json" => SearchParser.Parse(response, SearchFilter.Artists, log),
        "search_playlists.json" => SearchParser.Parse(response, SearchFilter.CommunityPlaylists, log),
        "search_suggestions.json" => SearchSuggestionsParser.Parse(response, log),
        "album.json" => AlbumParser.Parse(response, "MPREb_K8qWMWVqXGi", log),
        "album_single.json" => AlbumParser.Parse(response, "MPREb_X1DQ1j0PPrX", log),
        "artist.json" => ArtistParser.Parse(response, "UCRr1xG_2WIDs18a6cIiCxeA", log),
        "artist_small.json" => ArtistParser.Parse(response, "UCLZ7tlKC06ResyDmEStSrOw", log),
        "artist_albums.json" or "artist_albums_paged.json" => ArtistParser.ParseDiscography(response, log),
        "artist_albums_continuation.json" => ArtistParser.ParseDiscographyContinuation(response, log),
        "explore.json" => ExploreParser.Parse(response, log),
        "mood_categories.json" => ExploreParser.ParseMoodCategories(response, log),
        "mood_playlists.json" or "genre_playlists.json" => ExploreParser.ParseMoodPlaylists(response, log),
        "new_releases_albums.json" => ExploreParser.ParseNewReleases(response, log),
        "new_releases_videos.json" => ExploreParser.ParseNewVideos(response, log),
        "charts_us.json" => ChartsParser.Parse(response, log),
        "playlist.json" => PlaylistParser.Parse(response, "PLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN", log),
        "playlist_continuation.json" => PlaylistParser.ParseContinuation(response, log),
        "watch_playlist.json" or "watch_radio.json" => WatchParser.Parse(response, log),
        "lyrics.json" or "lyrics_timed.json" or "lyrics_timed_unsynced.json" => LyricsParser.Parse(response, log),
        "related.json" => RelatedParser.Parse(response, log),
        // Unplayable and age-gated tracks legitimately have no loudness; the parser must still not warn.
        "player.json" or "player_error.json" or "player_login_required.json" => new { LoudnessDb = PlayerParser.ParseLoudnessDb(response, log) },
        _ => throw new InvalidOperationException($"No parser mapped for fixture {fixture}; add it here."),
    };
}
