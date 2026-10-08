using System.Net;
using System.Text.Json.Nodes;
using HushMusic.Core;
using HushMusic.InnerTube.Http;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

public sealed class InnerTubeClientTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Anonymous_request_uses_alt_json_and_prettyPrint_without_key()
    {
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject { ["browseId"] = "FEmusic_home" }), Ct);

        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://music.youtube.com/youtubei/v1/browse?alt=json&prettyPrint=false", request.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Cookie_mode_adds_the_public_key_like_ytmusicapi()
    {
        _host.SignIn();

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject { ["browseId"] = "FEmusic_home" }), Ct);

        Assert.Equal(
            "https://music.youtube.com/youtubei/v1/browse?alt=json&key=AIzaSyC9XL3ZjWddXya6X74dJoCTL-WEYFDNX30&prettyPrint=false",
            _host.Handler.Last.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Endpoint_with_slash_is_kept_in_the_path()
    {
        await _host.Client.PostAsync(new InnerTubeRequest("music/get_search_suggestions", new JsonObject { ["input"] = "daft p" }), Ct);

        Assert.Equal("/youtubei/v1/music/get_search_suggestions", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Query_continuation_is_appended_raw_twice()
    {
        var token = "4qmFsgKhAhIMRkVtdXNpY19ob21l%3D%3D";

        await _host.Client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["browseId"] = "FEmusic_home" }) { QueryContinuation = token },
            Ct);

        Assert.EndsWith($"?alt=json&prettyPrint=false&ctoken={token}&continuation={token}", _host.Handler.Last.Uri.OriginalString);
    }

    [Fact]
    public async Task Body_gets_context_appended_after_endpoint_fields()
    {
        var body = new JsonObject { ["query"] = "daft punk", ["params"] = "EgWKAQIIAWoMEA4QChADEAQQCRAF" };

        await _host.Client.PostAsync(new InnerTubeRequest("search", body), Ct);

        Assert.Equal(
            """{"query":"daft punk","params":"EgWKAQIIAWoMEA4QChADEAQQCRAF","context":{"client":{"clientName":"WEB_REMIX","clientVersion":"1.20261007.01.00","hl":"en"},"user":{}}}""",
            _host.Handler.Last.Body);
        Assert.False(body.ContainsKey("context"), "the caller's body must not be modified");
    }

    [Fact]
    public async Task Content_location_becomes_gl_before_hl()
    {
        _host.Settings.Current.ContentLocation = "ro";

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        Assert.Equal(
            """{"clientName":"WEB_REMIX","clientVersion":"1.20261007.01.00","gl":"RO","hl":"en"}""",
            _host.Handler.Last.Json["context"]!["client"]!.ToJsonString());
    }

    [Theory]
    [InlineData("2026-10-07T00:00:00Z", "1.20261007.01.00")]
    [InlineData("2026-10-07T23:59:59Z", "1.20261007.01.00")]
    [InlineData("2026-10-08T01:00:00+03:00", "1.20261007.01.00")]
    [InlineData("2027-01-01T00:00:00Z", "1.20270101.01.00")]
    public void Client_version_is_one_dot_utc_date_dot_01_dot_00(string now, string expected)
    {
        Assert.Equal(expected, ClientContext.ClientVersion(DateTimeOffset.Parse(now, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Mobile_profile_swaps_only_client_name_and_version()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(
            """{"client":{"clientName":"ANDROID_MUSIC","clientVersion":"7.21.50","gl":"US","hl":"en"},"user":{}}""",
            ClientContext.Create(now, "us", ClientProfile.Mobile).ToJsonString());
        Assert.Equal(
            """{"client":{"clientName":"WEB_REMIX","clientVersion":"1.20261007.01.00","gl":"US","hl":"en"},"user":{}}""",
            ClientContext.Create(now, "us").ToJsonString());
    }

    [Fact]
    public async Task Client_profile_applies_to_that_request_only()
    {
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()) { Client = ClientProfile.Mobile }, Ct);
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        Assert.Equal("ANDROID_MUSIC", _host.Handler.Requests[0].Json["context"]!["client"]!["clientName"]!.GetValue<string>());
        Assert.Equal("WEB_REMIX", _host.Handler.Requests[1].Json["context"]!["client"]!["clientName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Anonymous_request_ignores_the_signed_in_account()
    {
        _host.SignIn();

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()) { Anonymous = true }, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("https://music.youtube.com/youtubei/v1/browse?alt=json&prettyPrint=false", request.Uri.AbsoluteUri);
        Assert.Equal("SOCS=CAI", request.Header("Cookie"));
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("X-Goog-AuthUser"));
        Assert.Equal(InnerTubeTestHost.VisitorId, request.Header("X-Goog-Visitor-Id"));
        Assert.Equal(0, _host.Auth.ApplyCount);
        Assert.Equal(0, _host.Auth.ResponseCount); // its Set-Cookie must not rotate the account cookies
    }

    [Fact]
    public async Task Anonymous_request_never_reports_the_session_as_expired()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Unauthenticated, HttpStatusCode.Unauthorized);
        _host.Handler.EnqueueJson(Responses.SignInPrompt);

        await Assert.ThrowsAsync<AuthRequiredException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()) { Anonymous = true }, Ct));
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()) { Anonymous = true }, Ct);

        Assert.Empty(_host.Auth.Rejections);
    }

    [Fact]
    public async Task Anonymous_auth_only_request_throws_before_sending()
    {
        _host.SignIn();

        await Assert.ThrowsAsync<AuthRequiredException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("like/like", new JsonObject()) { RequiresAuth = true, Anonymous = true }, Ct));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Anonymous_request_sends_ytmusicapi_headers_and_consent_cookie()
    {
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        var request = _host.Handler.Last;
        Assert.Equal("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:88.0) Gecko/20100101 Firefox/88.0", request.Header("User-Agent"));
        Assert.Equal("*/*", request.Header("Accept"));
        Assert.Equal("https://music.youtube.com", request.Header("Origin"));
        Assert.Equal("application/json", request.Header("Content-Type"));
        Assert.Equal("gzip", request.Header("Content-Encoding"));
        Assert.Equal(InnerTubeTestHost.VisitorId, request.Header("X-Goog-Visitor-Id"));
        Assert.Equal("SOCS=CAI", request.Header("Cookie"));
        Assert.Null(request.Header("Authorization"));
        Assert.Null(request.Header("X-Origin"));
    }

    [Fact]
    public async Task Every_request_goes_through_the_authenticator()
    {
        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);
        await _host.Client.PostAsync(new InnerTubeRequest("search", new JsonObject()), Ct);

        Assert.Equal(2, _host.Auth.ApplyCount);
        Assert.Equal(2, _host.Auth.ResponseCount);
    }

    [Fact]
    public async Task Authenticated_request_sends_account_cookie_instead_of_consent_cookie()
    {
        _host.SignIn();

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        var request = _host.Handler.Last;
        Assert.Equal(FakeAuthenticator.CookieHeader, request.Header("Cookie"));
        Assert.StartsWith("SAPISIDHASH ", request.Header("Authorization"));
        Assert.Equal("0", request.Header("X-Goog-AuthUser"));
        Assert.Equal(InnerTubeTestHost.VisitorId, request.Header("X-Goog-Visitor-Id"));
    }

    [Fact]
    public async Task Auth_only_request_throws_before_sending_when_signed_out()
    {
        await Assert.ThrowsAsync<AuthRequiredException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("like/like", new JsonObject()) { RequiresAuth = true }, Ct));

        Assert.Empty(_host.Handler.Requests);
        Assert.Equal(0, _host.Auth.ApplyCount);
    }

    [Fact]
    public async Task Http_error_maps_to_InnerTubeException_with_error_message()
    {
        _host.Handler.EnqueueJson(Responses.InvalidArgument, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Equal("browse", ex.Endpoint);
        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("Request contains an invalid argument.", ex.Message);
        Assert.Contains("HTTP 400", ex.Message);
    }

    [Fact]
    public async Task Http_error_without_json_body_still_maps()
    {
        _host.Handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("<html>404</html>") });

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Equal(404, ex.StatusCode);
    }

    [Fact]
    public async Task Client_errors_are_not_retried()
    {
        _host.Handler.EnqueueJson(Responses.InvalidArgument, HttpStatusCode.BadRequest);

        await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task Server_error_is_retried_once_then_succeeds()
    {
        _host.Handler.EnqueueStatus(HttpStatusCode.ServiceUnavailable);
        _host.Handler.EnqueueJson("""{"ok":true}""");

        var json = await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        Assert.True(json["ok"]!.GetValue<bool>());
        Assert.Equal(2, _host.Handler.Requests.Count);
        Assert.Equal(_host.Handler.Requests[0].Body, _host.Handler.Requests[1].Body);
        Assert.Equal(2, _host.Auth.ApplyCount);
    }

    [Fact]
    public async Task Server_error_twice_maps_to_InnerTubeException()
    {
        _host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);
        _host.Handler.EnqueueStatus(HttpStatusCode.BadGateway);

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("next", new JsonObject()), Ct));

        Assert.Equal(502, ex.StatusCode);
        Assert.Equal(2, _host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Network_error_is_retried_once()
    {
        _host.Handler.EnqueueNetworkError();
        _host.Handler.EnqueueJson("{}");

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        Assert.Equal(2, _host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Network_error_twice_maps_to_InnerTubeException_without_status()
    {
        _host.Handler.EnqueueNetworkError();
        _host.Handler.EnqueueNetworkError();

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Null(ex.StatusCode);
        Assert.IsType<HttpRequestException>(ex.InnerException);
    }

    [Fact]
    public async Task Non_idempotent_request_is_not_retried()
    {
        _host.SignIn();
        _host.Handler.EnqueueStatus(HttpStatusCode.InternalServerError);

        await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("playlist/create", new JsonObject()) { RequiresAuth = true, AllowRetry = false }, Ct));

        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task Invalid_json_maps_to_InnerTubeException()
    {
        _host.Handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") });

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Equal(200, ex.StatusCode);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_wrapped()
    {
        using var cts = new CancellationTokenSource();
        _host.Handler.Enqueue(_ =>
        {
            cts.Cancel();
            throw new TaskCanceledException();
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), cts.Token));

        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task Timeout_is_retried_once_then_mapped()
    {
        _host.Handler.Enqueue(_ => throw new TaskCanceledException("timeout", new TimeoutException()));
        _host.Handler.Enqueue(_ => throw new TaskCanceledException("timeout", new TimeoutException()));

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Contains("did not respond", ex.Message);
        Assert.Equal(2, _host.Handler.Requests.Count);
    }

    [Fact]
    public async Task Tracking_ping_is_an_authenticated_get()
    {
        _host.SignIn();
        _host.Handler.EnqueueStatus(HttpStatusCode.NoContent);

        await _host.Client.SendTrackingPingAsync(new Uri("https://s.youtube.com/api/stats/playback?ns=yt&docid=abc&ver=2&c=WEB_REMIX&cpn=x"), Ct);

        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("s.youtube.com", request.Uri.Host);
        Assert.Equal(FakeAuthenticator.CookieHeader, request.Header("Cookie"));
        Assert.StartsWith("SAPISIDHASH ", request.Header("Authorization"));
        Assert.Null(request.Body);
    }

    [Theory]
    [InlineData("https://evil.example.com/api/stats/playback")]
    [InlineData("http://s.youtube.com/api/stats/playback")]
    [InlineData("https://s.youtube.com.evil.example/api/stats/playback")]
    public async Task Tracking_ping_refuses_other_hosts(string url)
    {
        _host.SignIn();

        await Assert.ThrowsAsync<InnerTubeException>(() => _host.Client.SendTrackingPingAsync(new Uri(url), Ct));

        Assert.Empty(_host.Handler.Requests);
    }
}
