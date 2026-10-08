using System.Net;
using System.Text.Json.Nodes;
using HushMusic.Core;
using HushMusic.InnerTube.Http;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

public sealed class SessionRejectionTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Http_401_while_signed_in_reports_rejection()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Unauthenticated, HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<AuthRequiredException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Equal(SessionRejection.ExpiredMessage, ex.Message);
        var reason = Assert.Single(_host.Auth.Rejections);
        Assert.Contains("You must be signed in", reason);
        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task Must_be_signed_in_message_with_other_status_reports_rejection()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"error":{"code":403,"message":"Unauthorized. You must be signed in."}}""", HttpStatusCode.Forbidden);

        await Assert.ThrowsAsync<AuthRequiredException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.Single(_host.Auth.Rejections);
    }

    [Fact]
    public async Task Http_401_while_signed_out_asks_for_sign_in_without_reporting()
    {
        _host.Handler.EnqueueJson(Responses.Unauthenticated, HttpStatusCode.Unauthorized);

        var ex = await Assert.ThrowsAsync<AuthRequiredException>(() =>
            _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct));

        Assert.NotEqual(SessionRejection.ExpiredMessage, ex.Message);
        Assert.Empty(_host.Auth.Rejections);
    }

    [Fact]
    public async Task Sign_in_prompt_instead_of_library_reports_rejection()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.SignInPrompt);

        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Library.GetHistoryAsync(Ct));

        var reason = Assert.Single(_host.Auth.Rejections);
        Assert.Contains("Sign in to view your history", reason);
    }

    [Fact]
    public async Task Sign_in_prompt_on_liked_songs_reports_rejection()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.SignInPrompt);

        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Library.GetLikedSongsAsync(null, Ct));

        Assert.Single(_host.Auth.Rejections);
    }

    [Fact]
    public async Task Signed_out_account_menu_reports_rejection()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.SignedOutAccountMenu);

        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.GetAccountInfoAsync(Ct));

        Assert.Contains("account menu", Assert.Single(_host.Auth.Rejections));
    }

    [Fact]
    public async Task Anonymous_home_is_not_checked()
    {
        _host.Handler.EnqueueJson(Responses.SignInPrompt);

        await _host.Client.PostAsync(new InnerTubeRequest("browse", new JsonObject()), Ct);

        Assert.Empty(_host.Auth.Rejections);
    }

    [Fact]
    public void Real_home_and_playlist_fixtures_are_not_sign_in_prompts()
    {
        foreach (var fixture in new[] { "home.json", "playlist.json", "album.json", "artist.json", "search_all.json" })
        {
            Assert.Null(SessionRejection.FindSignInPrompt(JsonNode.Parse(Fixtures.Read(fixture))!));
        }
    }
}
