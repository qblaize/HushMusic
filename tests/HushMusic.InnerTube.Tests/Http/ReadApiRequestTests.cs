using HushMusic.Core;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Api;
using HushMusic.InnerTube.Http;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>Request shapes of the read APIs. Golden bodies are the ones ytmusicapi sent (Fixtures/EXPECTED.md).</summary>
public sealed class ReadApiRequestTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Home_browses_FEmusic_home()
    {
        await _host.Browse.GetHomeAsync(null, Ct);

        Assert.Equal("""{"browseId":"FEmusic_home"}""", _host.Handler.Last.BodyWithoutContext);
        Assert.EndsWith("/browse", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Theory]
    [InlineData(SearchFilter.All, """{"query":"daft punk"}""")]
    [InlineData(SearchFilter.Songs, """{"query":"daft punk","params":"EgWKAQIIAWoMEA4QChADEAQQCRAF"}""")]
    [InlineData(SearchFilter.Videos, """{"query":"daft punk","params":"EgWKAQIQAWoMEA4QChADEAQQCRAF"}""")]
    [InlineData(SearchFilter.Albums, """{"query":"daft punk","params":"EgWKAQIYAWoMEA4QChADEAQQCRAF"}""")]
    [InlineData(SearchFilter.Artists, """{"query":"daft punk","params":"EgWKAQIgAWoMEA4QChADEAQQCRAF"}""")]
    [InlineData(SearchFilter.CommunityPlaylists, """{"query":"daft punk","params":"EgeKAQQoAEABagwQDhAKEAMQBBAJEAU%3D"}""")]
    [InlineData(SearchFilter.FeaturedPlaylists, """{"query":"daft punk","params":"EgeKAQQoADgBagwQDhAKEAMQBBAJEAU%3D"}""")]
    public async Task Search_sends_ytmusicapi_filter_params(SearchFilter filter, string expectedBody)
    {
        await _host.Search.SearchAsync("daft punk", filter, null, Ct);

        Assert.Equal(expectedBody, _host.Handler.Last.BodyWithoutContext);
        Assert.Equal("/youtubei/v1/search", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Blank_search_does_not_hit_the_network()
    {
        var results = await _host.Search.SearchAsync("  ", SearchFilter.Songs, null, Ct);
        var suggestions = await _host.Search.GetSuggestionsAsync("", Ct);

        Assert.Empty(results.Sections);
        Assert.Same(SearchSuggestions.Empty, suggestions);
        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Suggestions_send_input()
    {
        await _host.Search.GetSuggestionsAsync("daft p", Ct);

        Assert.Equal("""{"input":"daft p"}""", _host.Handler.Last.BodyWithoutContext);
        Assert.Equal("/youtubei/v1/music/get_search_suggestions", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Search_continuation_resends_query_and_params_with_ctoken()
    {
        var body = new System.Text.Json.Nodes.JsonObject { ["query"] = "daft punk", ["params"] = "EgWKAQIIAWoMEA4QChADEAQQCRAF" };
        var token = ContinuationToken.Wrap(ContinuationScope.Search, "EqIDEglkYWZ0%3D", body, nameof(SearchFilter.Songs))!;

        await _host.Search.SearchAsync("ignored", SearchFilter.All, token, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("""{"query":"daft punk","params":"EgWKAQIIAWoMEA4QChADEAQQCRAF"}""", request.BodyWithoutContext);
        Assert.EndsWith("&ctoken=EqIDEglkYWZ0%3D&continuation=EqIDEglkYWZ0%3D", request.Uri.OriginalString);
    }

    [Fact]
    public async Task Unfiltered_search_has_no_continuation()
    {
        _host.Handler.EnqueueFixture("search_all.json");

        var results = await _host.Search.SearchAsync("daft punk", SearchFilter.All, null, Ct);

        Assert.Null(results.Continuation);
    }

    [Fact]
    public async Task Album_browses_its_MPRE_id_and_rejects_others()
    {
        await _host.Browse.GetAlbumAsync("MPREb_K8qWMWVqXGi", Ct);
        Assert.Equal("""{"browseId":"MPREb_K8qWMWVqXGi"}""", _host.Handler.Last.BodyWithoutContext);

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Browse.GetAlbumAsync("OLAK5uy_lNVBcjNtiCwq", Ct));
        Assert.Single(_host.Handler.Requests);
    }

    [Theory]
    [InlineData("UCRr1xG_2WIDs18a6cIiCxeA")]
    [InlineData("MPLAUCRr1xG_2WIDs18a6cIiCxeA")]
    public async Task Artist_browses_channel_id_without_MPLA(string id)
    {
        await _host.Browse.GetArtistAsync(id, Ct);

        Assert.Equal("""{"browseId":"UCRr1xG_2WIDs18a6cIiCxeA"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Theory]
    [InlineData("PLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN")]
    [InlineData("VLPLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN")]
    public async Task Playlist_adds_VL_once(string id)
    {
        await _host.Browse.GetPlaylistAsync(id, Ct);

        Assert.Equal("""{"browseId":"VLPLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Playlist_tracks_continuation_is_body_style()
    {
        var token = ContinuationToken.Wrap(ContinuationScope.Playlist, "4qmFsgKHARIk%3D%3D")!;

        await _host.Browse.GetPlaylistTracksAsync(token, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("""{"continuation":"4qmFsgKHARIk%3D%3D"}""", request.BodyWithoutContext);
        Assert.Equal("?alt=json&prettyPrint=false", request.Uri.Query);
    }

    [Fact]
    public async Task Lyrics_browse_the_lyrics_id_and_skip_empty_ids()
    {
        Assert.Null(await _host.Watch.GetLyricsAsync("", cancellationToken: Ct));
        Assert.Empty(_host.Handler.Requests);

        await _host.Watch.GetLyricsAsync("MPLYt_PITqkpE6ExP-3", cancellationToken: Ct);
        Assert.Equal("""{"browseId":"MPLYt_PITqkpE6ExP-3"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Watch_for_a_song_matches_ytmusicapi()
    {
        _host.Handler.EnqueueJson(Responses.EmptyQueue);

        await _host.Watch.GetWatchPlaylistAsync("hpSrLjc5SMs", cancellationToken: Ct);

        Assert.Equal(
            """{"enablePersistentPlaylistPanel":true,"isAudioOnly":true,"tunerSettingValue":"AUTOMIX_SETTING_NORMAL","videoId":"hpSrLjc5SMs","watchEndpointMusicSupportedConfigs":{"watchEndpointMusicConfig":{"hasPersistentPlaylistPanel":true,"musicVideoType":"MUSIC_VIDEO_TYPE_ATV"}},"playlistId":"RDAMVMhpSrLjc5SMs"}""",
            _host.Handler.Last.BodyWithoutContext);
        Assert.Equal("/youtubei/v1/next", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Watch_radio_matches_ytmusicapi()
    {
        _host.Handler.EnqueueJson(Responses.EmptyQueue);

        await _host.Watch.GetWatchPlaylistAsync("hpSrLjc5SMs", radio: true, cancellationToken: Ct);

        Assert.Equal(
            """{"enablePersistentPlaylistPanel":true,"isAudioOnly":true,"tunerSettingValue":"AUTOMIX_SETTING_NORMAL","videoId":"hpSrLjc5SMs","playlistId":"RDAMVMhpSrLjc5SMs","params":"wAEB"}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public void Watch_shuffle_playlist_strips_VL_and_sets_shuffle_params()
    {
        var body = WatchApi.BuildNextBody(null, "VLPLabc", radio: false, shuffle: true);

        Assert.Equal(
            """{"enablePersistentPlaylistPanel":true,"isAudioOnly":true,"tunerSettingValue":"AUTOMIX_SETTING_NORMAL","playlistId":"PLabc","params":"wAEB8gECKAE%3D"}""",
            body.ToJsonString());
    }

    [Fact]
    public void Watch_radio_wins_over_shuffle()
    {
        var body = WatchApi.BuildNextBody(null, "RDAMPLOLAK5uy_x", radio: true, shuffle: true);

        Assert.Equal("wAEB", body["params"]!.GetValue<string>());
        Assert.Equal("RDAMPLOLAK5uy_x", body["playlistId"]!.GetValue<string>());
    }

    [Fact]
    public void Watch_album_queue_keeps_video_and_playlist()
    {
        var body = WatchApi.BuildNextBody("IluRBvnYMoY", "OLAK5uy_lNVBcjNtiCwq", radio: false, shuffle: false);

        Assert.Equal("OLAK5uy_lNVBcjNtiCwq", body["playlistId"]!.GetValue<string>());
        Assert.NotNull(body["watchEndpointMusicSupportedConfigs"]);
        Assert.False(body.ContainsKey("params"));
    }

    [Fact]
    public void Watch_needs_a_video_or_a_playlist()
    {
        Assert.Throws<ArgumentException>(() => WatchApi.BuildNextBody(null, null, false, false));
    }

    [Fact]
    public async Task Watch_without_queue_content_throws()
    {
        _host.Handler.EnqueueJson("{}");

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Watch.GetWatchPlaylistAsync(null, "PLprivate", cancellationToken: Ct));

        Assert.Contains("PLprivate", ex.Message);
    }

    [Fact]
    public async Task Watch_continuation_resends_the_same_body_with_ctoken()
    {
        var body = WatchApi.BuildNextBody("hpSrLjc5SMs", null, radio: true, shuffle: false);
        var token = ContinuationToken.Wrap(ContinuationScope.Watch, "CDISOBIL%3D", body)!;

        await _host.Watch.GetWatchPlaylistContinuationAsync(token, Ct);

        var request = _host.Handler.Last;
        Assert.Equal(body.ToJsonString(), request.BodyWithoutContext);
        Assert.EndsWith("&ctoken=CDISOBIL%3D&continuation=CDISOBIL%3D", request.Uri.OriginalString);
    }

    [Theory]
    [InlineData("library.playlists", "FEmusic_liked_playlists")]
    [InlineData("library.songs", "FEmusic_liked_videos")]
    [InlineData("library.albums", "FEmusic_liked_albums")]
    [InlineData("library.artists", "FEmusic_library_corpus_track_artists")]
    [InlineData("liked", "VLLM")]
    [InlineData("history", "FEmusic_history")]
    public async Task Library_pages_browse_ytmusicapi_ids_when_signed_in(string page, string browseId)
    {
        _host.SignIn();

        await LoadLibraryPage(page);

        Assert.Equal($$"""{"browseId":"{{browseId}}"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Theory]
    [InlineData("library.playlists")]
    [InlineData("library.songs")]
    [InlineData("library.albums")]
    [InlineData("library.artists")]
    [InlineData("liked")]
    [InlineData("history")]
    public async Task Library_pages_require_sign_in(string page)
    {
        await Assert.ThrowsAsync<AuthRequiredException>(() => LoadLibraryPage(page));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Library_continuation_resends_browse_id_with_ctoken()
    {
        _host.SignIn();
        var token = ContinuationToken.Wrap(
            ContinuationScope.LibrarySongs,
            "4qmFsgJ%3D",
            new System.Text.Json.Nodes.JsonObject { ["browseId"] = "FEmusic_liked_videos" })!;

        await _host.Library.GetSongsAsync(token, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("""{"browseId":"FEmusic_liked_videos"}""", request.BodyWithoutContext);
        Assert.EndsWith("&ctoken=4qmFsgJ%3D&continuation=4qmFsgJ%3D", request.Uri.OriginalString);
    }

    [Fact]
    public async Task Liked_songs_continuation_uses_the_playlist_path()
    {
        _host.SignIn();
        var token = ContinuationToken.Wrap(ContinuationScope.Playlist, "4qmFsgLM")!;

        await _host.Library.GetLikedSongsAsync(token, Ct);

        Assert.Equal("""{"continuation":"4qmFsgLM"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Continuation_from_another_method_is_rejected()
    {
        var token = ContinuationToken.Wrap(ContinuationScope.Watch, "T", new System.Text.Json.Nodes.JsonObject())!;

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Browse.GetHomeAsync(token, Ct));
        Assert.Empty(_host.Handler.Requests);
    }

    private Task LoadLibraryPage(string page) => page switch
    {
        "library.playlists" => _host.Library.GetPlaylistsAsync(null, Ct),
        "library.songs" => _host.Library.GetSongsAsync(null, Ct),
        "library.albums" => _host.Library.GetAlbumsAsync(null, Ct),
        "library.artists" => _host.Library.GetArtistsAsync(null, Ct),
        "liked" => _host.Library.GetLikedSongsAsync(null, Ct),
        "history" => _host.Library.GetHistoryAsync(Ct),
        _ => throw new ArgumentOutOfRangeException(nameof(page)),
    };
}
