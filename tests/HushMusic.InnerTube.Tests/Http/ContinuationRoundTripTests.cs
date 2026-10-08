using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>
/// Real fixture -> parser -> opaque token -> next request. Proves the raw InnerTube token and the original
/// request context survive the round trip. Skipped while a parser still returns no continuation.
/// </summary>
public sealed class ContinuationRoundTripTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Home_continuation_resends_FEmusic_home_with_the_fixture_ctoken()
    {
        var raw = JsonLookup.GetString(
            FixtureJson("home.json"),
            "contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer",
            "continuations", 0, "nextContinuationData", "continuation")!;
        _host.Handler.EnqueueFixture("home.json");

        var page = await _host.Browse.GetHomeAsync(null, Ct);
        Assert.SkipWhen(page.Continuation is null, "HomeParser returns no continuation yet.");

        await _host.Browse.GetHomeAsync(page.Continuation, Ct);

        AssertQueryContinuation(_host.Handler.Last, raw, """{"browseId":"FEmusic_home"}""");
    }

    [Fact]
    public async Task Filtered_search_continuation_resends_query_and_params()
    {
        var raw = FirstSearchShelfToken(FixtureJson("search_songs.json"));
        _host.Handler.EnqueueFixture("search_songs.json");

        var results = await _host.Search.SearchAsync("daft punk", SearchFilter.Songs, null, Ct);
        Assert.SkipWhen(results.Continuation is null, "SearchParser returns no continuation yet.");

        _host.Handler.EnqueueFixture("search_songs_continuation.json");
        await _host.Search.SearchAsync("daft punk", SearchFilter.Songs, results.Continuation, Ct);

        AssertQueryContinuation(_host.Handler.Last, raw, """{"query":"daft punk","params":"EgWKAQIIAWoMEA4QChADEAQQCRAF"}""");
    }

    [Fact]
    public async Task Playlist_continuation_sends_only_the_token_as_ytmusicapi_did()
    {
        _host.Handler.EnqueueFixture("playlist.json");

        var page = await _host.Browse.GetPlaylistAsync("PLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN", Ct);
        Assert.SkipWhen(page.Tracks.Continuation is null, "PlaylistParser returns no continuation yet.");

        _host.Handler.EnqueueFixture("playlist_continuation.json");
        await _host.Browse.GetPlaylistTracksAsync(page.Tracks.Continuation!, Ct);

        // Body from Fixtures/EXPECTED.md (playlist_continuation.json request).
        Assert.Equal(
            """{"continuation":"4qmFsgKHARIkVkxQTHdfOEk3ajZfUUZvZ2NORkEtWmd3bkR6N1g4cnZuVlVOGjplaDVRVkRwRFIxRnBSVVJSTlU1RWJFTlBWVkYzVDBSTk0xRlZSVEZSYWtHU0FRTUl1Z1R3QVFBJTNEmgIiUEx3XzhJN2o2X1FGb2djTkZBLVpnd25EejdYOHJ2blZVTg%3D%3D"}""",
            _host.Handler.Last.BodyWithoutContext);
        Assert.Equal("?alt=json&prettyPrint=false", _host.Handler.Last.Uri.Query);
    }

    [Fact]
    public async Task Radio_continuation_resends_the_radio_body()
    {
        var raw = JsonLookup.GetString(
            FixtureJson("watch_radio.json"),
            "contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer", "watchNextTabbedResultsRenderer",
            "tabs", 0, "tabRenderer", "content", "musicQueueRenderer", "content", "playlistPanelRenderer",
            "continuations", 0, "nextRadioContinuationData", "continuation")!;
        _host.Handler.EnqueueFixture("watch_radio.json");

        var queue = await _host.Watch.GetWatchPlaylistAsync("hpSrLjc5SMs", radio: true, cancellationToken: Ct);
        Assert.SkipWhen(queue.Continuation is null, "WatchParser returns no continuation yet.");

        await _host.Watch.GetWatchPlaylistContinuationAsync(queue.Continuation!, Ct);

        AssertQueryContinuation(
            _host.Handler.Last,
            raw,
            """{"enablePersistentPlaylistPanel":true,"isAudioOnly":true,"tunerSettingValue":"AUTOMIX_SETTING_NORMAL","videoId":"hpSrLjc5SMs","playlistId":"RDAMVMhpSrLjc5SMs","params":"wAEB"}""");
    }

    private static JsonNode FixtureJson(string name) => JsonNode.Parse(Fixtures.Read(name))!;

    private static string FirstSearchShelfToken(JsonNode response)
    {
        var sections = JsonLookup.Get(
            response, "contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents") as JsonArray;
        return sections!
            .Select(s => JsonLookup.GetString(s, "musicShelfRenderer", "continuations", 0, "nextContinuationData", "continuation"))
            .First(t => t is not null)!;
    }

    private static void AssertQueryContinuation(RecordedRequest request, string rawToken, string expectedBody)
    {
        Assert.Equal(expectedBody, request.BodyWithoutContext);
        Assert.EndsWith($"&ctoken={rawToken}&continuation={rawToken}", request.Uri.OriginalString);
    }
}
