using System.Net;
using HushMusic.Core;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Api;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>Request shapes of account and write actions (ytmusicapi mixins/library.py and mixins/playlists.py).</summary>
public sealed class AccountApiRequestTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Theory]
    [InlineData(LikeStatus.Like, "/youtubei/v1/like/like")]
    [InlineData(LikeStatus.Dislike, "/youtubei/v1/like/dislike")]
    [InlineData(LikeStatus.Indifferent, "/youtubei/v1/like/removelike")]
    public async Task Rate_song_uses_the_like_endpoints(LikeStatus status, string path)
    {
        _host.SignIn();

        await _host.Account.RateSongAsync("hpSrLjc5SMs", status, Ct);

        Assert.Equal(path, _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal("""{"target":{"videoId":"hpSrLjc5SMs"}}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Write_actions_require_sign_in()
    {
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.RateSongAsync("v", LikeStatus.Like, Ct));
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.CreatePlaylistAsync("t", null, PrivacyStatus.Private, null, Ct));
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.AddPlaylistItemsAsync("PL1", ["v"], false, Ct));
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.EditPlaylistAsync("PL1", "t", cancellationToken: Ct));
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.DeletePlaylistAsync("PL1", Ct));
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.AddHistoryItemAsync("v", Ct));
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.GetAccountInfoAsync(Ct));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Gated_write_is_reported_as_not_performed()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"actions":[{"showEngagementPanelEndpoint":{"identifier":{"tag":"PAyoutube_music_restriction"}}}]}""");

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.RateSongAsync("v", LikeStatus.Like, Ct));

        Assert.Contains("PAyoutube_music_restriction", ex.Message);
    }

    [Fact]
    public async Task Create_playlist_body_matches_ytmusicapi_and_returns_id()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"playlistId":"PLnew123"}""");

        var id = await _host.Account.CreatePlaylistAsync("My list", "Hello <b>world</b>", PrivacyStatus.Unlisted, ["a1", "b2"], Ct);

        Assert.Equal("PLnew123", id);
        Assert.Equal("/youtubei/v1/playlist/create", _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal(
            """{"title":"My list","description":"Hello world","privacyStatus":"UNLISTED","videoIds":["a1","b2"]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Create_playlist_without_videos_omits_videoIds()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"playlistId":"PLnew"}""");

        await _host.Account.CreatePlaylistAsync("Empty", null, PrivacyStatus.Private, null, Ct);

        Assert.Equal("""{"title":"Empty","description":"","privacyStatus":"PRIVATE"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Create_playlist_rejects_angle_brackets_in_title()
    {
        _host.SignIn();

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Account.CreatePlaylistAsync("a<b", null, PrivacyStatus.Private, null, Ct));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Create_playlist_is_not_retried_on_server_error()
    {
        _host.SignIn();
        _host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.CreatePlaylistAsync("t", null, PrivacyStatus.Private, null, Ct));

        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task Create_playlist_without_id_throws()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("{}");

        await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.CreatePlaylistAsync("t", null, PrivacyStatus.Private, null, Ct));
    }

    [Fact]
    public async Task Add_items_strips_VL_and_adds_one_action_per_video()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);

        await _host.Account.AddPlaylistItemsAsync("VLPL1", ["a1", "b2"], allowDuplicates: false, Ct);

        Assert.Equal("/youtubei/v1/browse/edit_playlist", _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_ADD_VIDEO","addedVideoId":"a1"},{"action":"ACTION_ADD_VIDEO","addedVideoId":"b2"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Add_items_with_duplicates_sets_dedupe_skip()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);

        await _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: true, Ct);

        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_ADD_VIDEO","addedVideoId":"a1","dedupeOption":"DEDUPE_OPTION_SKIP"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Add_items_failure_status_throws()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"status":"STATUS_FAILED"}""");

        // Without allowDuplicates a failed status means "already in the playlist" (PlaylistDuplicateTests).
        var ex = await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: true, Ct));

        Assert.Contains("STATUS_FAILED", ex.Message);
    }

    [Fact]
    public async Task Add_items_needs_at_least_one_video()
    {
        _host.SignIn();

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Account.AddPlaylistItemsAsync("PL1", [], false, Ct));
    }

    [Fact]
    public async Task Remove_items_sends_setVideoId_pairs_and_skips_tracks_without_one()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);
        Track[] tracks =
        [
            new() { Title = "a", VideoId = "a1", SetVideoId = "56B44F6D10557CC6" },
            new() { Title = "b", VideoId = "b2" },
        ];

        await _host.Account.RemovePlaylistItemsAsync("PL1", tracks, Ct);

        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"setVideoId":"56B44F6D10557CC6","removedVideoId":"a1","action":"ACTION_REMOVE_VIDEO"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Remove_items_without_setVideoIds_throws_before_sending()
    {
        _host.SignIn();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _host.Account.RemovePlaylistItemsAsync("PL1", [new Track { Title = "b", VideoId = "b2" }], Ct));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Edit_playlist_batches_title_description_privacy()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);

        await _host.Account.EditPlaylistAsync("VLPL1", "New title", "New description", PrivacyStatus.Public, Ct);

        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_SET_PLAYLIST_NAME","playlistName":"New title"},{"action":"ACTION_SET_PLAYLIST_DESCRIPTION","playlistDescription":"New description"},{"action":"ACTION_SET_PLAYLIST_PRIVACY","playlistPrivacy":"PUBLIC"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Edit_playlist_with_nothing_to_change_sends_nothing()
    {
        _host.SignIn();

        await _host.Account.EditPlaylistAsync("PL1", cancellationToken: Ct);

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Delete_playlist_strips_VL()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);

        await _host.Account.DeletePlaylistAsync("VLPL1", Ct);

        Assert.Equal("/youtubei/v1/playlist/delete", _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal("""{"playlistId":"PL1"}""", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Account_info_posts_empty_body_to_account_menu()
    {
        _host.SignIn();

        await _host.Account.GetAccountInfoAsync(Ct);

        Assert.Equal("/youtubei/v1/account/account_menu", _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal("{}", _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public void Signature_timestamp_is_days_since_epoch_minus_one()
    {
        Assert.Equal(20732, PlayerRequest.SignatureTimestamp(new DateTimeOffset(2026, 10, 7, 23, 59, 0, TimeSpan.Zero)));
    }

    [Fact]
    public async Task Add_history_item_calls_player_then_pings_tracking_url()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(
            """{"playbackTracking":{"videostatsPlaybackUrl":{"baseUrl":"https://s.youtube.com/api/stats/playback?cl=1&docid=hpSrLjc5SMs&ei=x"}}}""");
        _host.Handler.EnqueueStatus(HttpStatusCode.NoContent);

        await _host.Account.AddHistoryItemAsync("hpSrLjc5SMs", Ct);

        Assert.Equal(2, _host.Handler.Requests.Count);
        var player = _host.Handler.Requests[0];
        Assert.Equal("/youtubei/v1/player", player.Uri.AbsolutePath);
        Assert.Equal(
            """{"playbackContext":{"contentPlaybackContext":{"signatureTimestamp":20732}},"video_id":"hpSrLjc5SMs"}""",
            player.BodyWithoutContext);

        var ping = _host.Handler.Requests[1];
        Assert.Equal(HttpMethod.Get, ping.Method);
        Assert.Matches(
            @"^https://s\.youtube\.com/api/stats/playback\?cl=1&docid=hpSrLjc5SMs&ei=x&ver=2&c=WEB_REMIX&cpn=[A-Za-z0-9_-]{16}$",
            ping.Uri.OriginalString);
        Assert.Equal(FakeAuthenticator.CookieHeader, ping.Header("Cookie"));
        Assert.Equal(2, _host.Auth.ApplyCount);
    }

    [Fact]
    public async Task Add_history_item_without_tracking_url_throws()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"playabilityStatus":{"status":"ERROR"}}""");

        await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.AddHistoryItemAsync("x", Ct));

        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public void Html_tags_are_stripped_like_html_to_txt()
    {
        Assert.Equal("a b c", AccountApi.StripHtmlTags("a <i>b</i> <br/>c"));
    }
}
