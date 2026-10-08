using System.Net;
using HushMusic.Core;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>Request shapes of timed lyrics, related and loudness (Fixtures/EXPECTED.md has the captured requests).</summary>
public sealed class WatchApiRequestTests : IDisposable
{
    private const string LyricsId = "MPLYt_PITqkpE6ExP-3";
    private const string WebClient = """{"clientName":"WEB_REMIX","clientVersion":"1.20261007.01.00","hl":"en"}""";
    private const string MobileClient = """{"clientName":"ANDROID_MUSIC","clientVersion":"7.21.50","hl":"en"}""";

    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static string ClientOf(RecordedRequest request) => request.Json["context"]!["client"]!.ToJsonString();

    [Fact]
    public async Task Plain_lyrics_are_one_web_client_browse()
    {
        _host.Handler.EnqueueFixture("lyrics.json");

        var lyrics = await _host.Watch.GetLyricsAsync(LyricsId, cancellationToken: Ct);

        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal($$"""{"browseId":"{{LyricsId}}"}""", request.BodyWithoutContext);
        Assert.Equal(WebClient, ClientOf(request));
        Assert.NotNull(lyrics);
        Assert.Null(lyrics.TimedLines);
    }

    [Fact]
    public async Task Timed_lyrics_send_the_same_browse_as_the_mobile_client()
    {
        _host.Handler.EnqueueFixture("lyrics_timed.json");

        var lyrics = await _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct);

        // ytmusicapi as_mobile(): only clientName/clientVersion change; headers stay the web ones.
        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal("/youtubei/v1/browse", request.Uri.AbsolutePath);
        Assert.Equal($$"""{"browseId":"{{LyricsId}}"}""", request.BodyWithoutContext);
        Assert.Equal($$$"""{"client":{{{MobileClient}}},"user":{}}""", request.Json["context"]!.ToJsonString());
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0", request.Header("User-Agent"));
        Assert.Equal("https://music.youtube.com", request.Header("Origin"));
        Assert.Equal(InnerTubeTestHost.VisitorId, request.Header("X-Goog-Visitor-Id"));

        Assert.NotNull(lyrics);
        Assert.NotNull(lyrics.TimedLines);
        Assert.Equal(40, lyrics.TimedLines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(22150), lyrics.TimedLines[1].Start);
    }

    [Fact]
    public async Task Mobile_client_keeps_the_content_location()
    {
        _host.Settings.Current.ContentLocation = "ro";
        _host.Handler.EnqueueFixture("lyrics_timed.json");

        await _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct);

        Assert.Equal("""{"clientName":"ANDROID_MUSIC","clientVersion":"7.21.50","gl":"RO","hl":"en"}""", ClientOf(_host.Handler.Last));
    }

    [Fact]
    public async Task Timed_lyrics_are_fetched_without_the_account_when_signed_in()
    {
        _host.SignIn();
        _host.Handler.EnqueueFixture("lyrics_timed.json");

        await _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("SOCS=CAI", request.Header("Cookie"));
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("X-Goog-AuthUser"));
        Assert.Equal("?alt=json&prettyPrint=false", request.Uri.Query);
        Assert.Equal(0, _host.Auth.ApplyCount);
        Assert.Equal(0, _host.Auth.ResponseCount);
    }

    [Fact]
    public async Task Unsynced_lyrics_come_from_the_mobile_response_as_plain_text()
    {
        _host.Handler.EnqueueFixture("lyrics_timed_unsynced.json");

        var lyrics = await _host.Watch.GetLyricsAsync("MPLYt_OZf7JU9TYTI-4", timestamps: true, Ct);

        Assert.Single(_host.Handler.Requests);
        Assert.NotNull(lyrics);
        Assert.Null(lyrics.TimedLines);
        Assert.StartsWith("Pana cand nu te iubeam,\nDorule, dorule,", lyrics.Text);
    }

    [Fact]
    public async Task Mobile_response_without_lyrics_falls_back_to_the_web_client()
    {
        _host.Handler.EnqueueJson("""{"contents":{}}""");
        _host.Handler.EnqueueFixture("lyrics.json");

        var lyrics = await _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct);

        Assert.Equal(2, _host.Handler.Requests.Count);
        Assert.Equal(MobileClient, ClientOf(_host.Handler.Requests[0]));
        Assert.Equal(WebClient, ClientOf(_host.Handler.Requests[1]));
        Assert.Equal(_host.Handler.Requests[0].BodyWithoutContext, _host.Handler.Requests[1].BodyWithoutContext);
        Assert.NotNull(lyrics);
        Assert.Null(lyrics.TimedLines);
        Assert.Equal("Source: LyricFind", lyrics.Source);
    }

    [Fact]
    public async Task Mobile_client_refused_by_the_server_falls_back_to_the_web_client()
    {
        _host.Handler.EnqueueJson(Responses.InvalidArgument, HttpStatusCode.BadRequest);
        _host.Handler.EnqueueFixture("lyrics.json");

        var lyrics = await _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct);

        Assert.Equal(2, _host.Handler.Requests.Count);
        Assert.NotNull(lyrics);
        Assert.StartsWith("Today is gonna be the day", lyrics.Text);
        Assert.Empty(_host.Auth.Rejections);
    }

    [Fact]
    public async Task Fallback_web_request_uses_the_account_when_signed_in()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"contents":{}}""");
        _host.Handler.EnqueueFixture("lyrics.json");

        await _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct);

        Assert.Equal(FakeAuthenticator.CookieHeader, _host.Handler.Last.Header("Cookie"));
        Assert.Equal(1, _host.Auth.ApplyCount);
    }

    [Fact]
    public async Task Network_failure_of_the_mobile_request_is_not_hidden()
    {
        _host.Handler.EnqueueNetworkError();
        _host.Handler.EnqueueNetworkError();

        await Assert.ThrowsAsync<InnerTubeException>(() => _host.Watch.GetLyricsAsync(LyricsId, timestamps: true, Ct));

        // One retry of the mobile request, no web request.
        Assert.Equal(2, _host.Handler.Requests.Count);
        Assert.All(_host.Handler.Requests, r => Assert.Equal(MobileClient, ClientOf(r)));
    }

    [Fact]
    public async Task Related_browses_the_MPTR_id_from_next()
    {
        _host.Handler.EnqueueFixture("related.json");

        var shelves = await _host.Watch.GetRelatedAsync("MPTRt_PITqkpE6ExP-3", Ct);

        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal("/youtubei/v1/browse", request.Uri.AbsolutePath);
        Assert.Equal("""{"browseId":"MPTRt_PITqkpE6ExP-3"}""", request.BodyWithoutContext);
        Assert.Equal(WebClient, ClientOf(request));
        Assert.Equal(6, shelves.Count);
        Assert.Equal(ShelfLayout.List, shelves[0].Layout);
    }

    [Fact]
    public async Task Related_with_the_account_when_signed_in()
    {
        _host.SignIn();
        _host.Handler.EnqueueFixture("related.json");

        await _host.Watch.GetRelatedAsync("MPTRt_PITqkpE6ExP-3", Ct);

        Assert.Equal(FakeAuthenticator.CookieHeader, _host.Handler.Last.Header("Cookie"));
    }

    [Fact]
    public async Task Blank_ids_do_not_hit_the_network()
    {
        Assert.Empty(await _host.Watch.GetRelatedAsync(" ", Ct));
        Assert.Null(await _host.Watch.GetLyricsAsync("", timestamps: true, Ct));
        Assert.Null(await _host.Watch.GetLoudnessDbAsync("", Ct));
        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Loudness_sends_the_get_song_player_request()
    {
        _host.Handler.EnqueueFixture("player.json");

        var loudness = await _host.Watch.GetLoudnessDbAsync("hpSrLjc5SMs", Ct);

        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal("/youtubei/v1/player", request.Uri.AbsolutePath);
        Assert.Equal(
            """{"playbackContext":{"contentPlaybackContext":{"signatureTimestamp":20732}},"video_id":"hpSrLjc5SMs"}""",
            request.BodyWithoutContext);
        Assert.Equal(WebClient, ClientOf(request));
        Assert.NotNull(loudness);
        Assert.Equal(-1.0799999, loudness.Value, 7);
    }

    [Fact]
    public async Task Loudness_is_fetched_without_the_account_when_signed_in()
    {
        _host.SignIn();
        _host.Handler.EnqueueFixture("player.json");

        await _host.Watch.GetLoudnessDbAsync("hpSrLjc5SMs", Ct);

        var request = _host.Handler.Last;
        Assert.Equal("SOCS=CAI", request.Header("Cookie"));
        Assert.Null(request.Header("Authorization"));
        Assert.Equal("?alt=json&prettyPrint=false", request.Uri.Query);
        Assert.Equal(0, _host.Auth.ApplyCount);
        Assert.Equal(0, _host.Auth.ResponseCount);
    }

    [Fact]
    public async Task Loudness_is_cached_per_video_including_unknown_values()
    {
        var watch = _host.Watch;
        _host.Handler.EnqueueFixture("player.json");
        _host.Handler.EnqueueFixture("player_error.json");

        var first = await watch.GetLoudnessDbAsync("hpSrLjc5SMs", Ct);
        var again = await watch.GetLoudnessDbAsync("hpSrLjc5SMs", Ct);
        var missing = await watch.GetLoudnessDbAsync("xxxxxxxxxxx", Ct);
        var missingAgain = await watch.GetLoudnessDbAsync("xxxxxxxxxxx", Ct);

        Assert.Equal(first, again);
        Assert.Null(missing);
        Assert.Null(missingAgain);
        Assert.Equal(2, _host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Loudness_cache_keeps_the_512_most_recent_videos()
    {
        var watch = _host.Watch;
        _host.Handler.Default = _ => FakeHttpHandler.Json("""{"playerConfig":{"audioConfig":{"loudnessDb":1.5}}}""");

        for (var i = 0; i <= 512; i++)
        {
            await watch.GetLoudnessDbAsync($"video{i:D6}", Ct);
        }

        Assert.Equal(513, _host.Handler.Requests.Count);

        await watch.GetLoudnessDbAsync("video000512", Ct); // still cached
        Assert.Equal(513, _host.Handler.Requests.Count);

        await watch.GetLoudnessDbAsync("video000000", Ct); // the oldest entry was evicted
        Assert.Equal(514, _host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Loudness_server_errors_propagate_and_are_not_cached()
    {
        var watch = _host.Watch;
        _host.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        _host.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        _host.Handler.EnqueueFixture("player.json");

        await Assert.ThrowsAsync<InnerTubeException>(() => watch.GetLoudnessDbAsync("hpSrLjc5SMs", Ct));
        var loudness = await watch.GetLoudnessDbAsync("hpSrLjc5SMs", Ct);

        Assert.NotNull(loudness);
        Assert.Equal(3, _host.Handler.Requests.Count);
    }
}
