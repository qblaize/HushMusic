using System.Text.Json.Nodes;
using HushMusic.InnerTube.Api;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

public sealed class ContinuationTokenTests
{
    [Fact]
    public void Round_trips_token_body_and_filter()
    {
        var body = new JsonObject { ["query"] = "daft punk", ["params"] = "EgWKAQIIAWoMEA4QChADEAQQCRAF" };

        var opaque = ContinuationToken.Wrap(ContinuationScope.Search, "EqIDEglk%3D", body, "Songs");
        var state = ContinuationToken.Unwrap(opaque!, ContinuationScope.Search);

        Assert.Equal("EqIDEglk%3D", state.Token);
        Assert.Equal(body.ToJsonString(), state.Body!.ToJsonString());
        Assert.Equal("Songs", state.Filter);
    }

    [Fact]
    public void Opaque_token_is_url_safe()
    {
        var opaque = ContinuationToken.Wrap(ContinuationScope.Playlist, "4qmFsgKHARIk+/==?&");

        Assert.Matches("^[A-Za-z0-9_-]+$", opaque!);
    }

    [Fact]
    public void Missing_raw_token_means_no_more_pages()
    {
        Assert.Null(ContinuationToken.Wrap(ContinuationScope.Home, null));
        Assert.Null(ContinuationToken.Wrap(ContinuationScope.Home, string.Empty));
    }

    [Fact]
    public void Token_from_another_method_is_rejected()
    {
        var opaque = ContinuationToken.Wrap(ContinuationScope.Home, "abc", new JsonObject());

        var ex = Assert.Throws<ArgumentException>(() => ContinuationToken.Unwrap(opaque!, ContinuationScope.Watch));
        Assert.Contains("home", ex.Message);
    }

    [Theory]
    [InlineData("not base64!")]
    [InlineData("bm90IGpzb24")]
    [InlineData("W10")]
    public void Garbage_is_rejected(string token)
    {
        Assert.Throws<ArgumentException>(() => ContinuationToken.Unwrap(token, ContinuationScope.Home));
    }

    [Fact]
    public void Body_style_token_has_no_body()
    {
        var state = ContinuationToken.Unwrap(ContinuationToken.Wrap(ContinuationScope.Playlist, "T")!, ContinuationScope.Playlist);

        Assert.Null(state.Body);
        Assert.Throws<ArgumentException>(() => state.RequireBody());
    }
}
