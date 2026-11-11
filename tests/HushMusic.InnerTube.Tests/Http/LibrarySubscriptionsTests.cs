using System.Net;
using HushMusic.Core;
using HushMusic.InnerTube.Api;
using HushMusic.InnerTube.Parsing;
using HushMusic.InnerTube.Tests.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>
/// ytmusicapi <c>get_library_subscriptions</c>: <c>browse {"browseId": "FEmusic_library_corpus_artists"}</c>, parsed like
/// <c>get_library_artists</c> (parse_library_artists), with classic <c>musicShelfContinuation</c> paging. The fixture is
/// hand-built from ytmusicapi's parser code, since the response needs a signed-in session.
/// </summary>
public sealed class LibrarySubscriptionsTests : IDisposable
{
    private const string Fixture = "Synthetic/synthetic_library_subscriptions.json";

    private readonly InnerTubeTestHost _host = new();
    private readonly CapturingLogger _log = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Subscriptions_browse_the_corpus_artists_page_when_signed_in()
    {
        _host.SignIn();

        await _host.Library.GetSubscriptionsAsync(null, Ct);

        Assert.Equal("""{"browseId":"FEmusic_library_corpus_artists"}""", _host.Handler.Last.BodyWithoutContext);
        Assert.Equal("/youtubei/v1/browse", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task Subscriptions_require_sign_in()
    {
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Library.GetSubscriptionsAsync(null, Ct));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Next_page_resends_the_browse_id_with_ctoken()
    {
        _host.SignIn();
        _host.Handler.EnqueueFixture(Fixture);
        var first = await _host.Library.GetSubscriptionsAsync(null, Ct);

        _host.Handler.EnqueueFixture("Synthetic/synthetic_library_artists_continuation.json");
        var next = await _host.Library.GetSubscriptionsAsync(first.Continuation, Ct);

        var request = _host.Handler.Last;
        Assert.Equal("""{"browseId":"FEmusic_library_corpus_artists"}""", request.BodyWithoutContext);
        Assert.EndsWith("&ctoken=SYNTHETIC_SUBSCRIPTIONS_TOKEN&continuation=SYNTHETIC_SUBSCRIPTIONS_TOKEN", request.Uri.OriginalString);
        Assert.Equal("Pharrell", Assert.Single(next.Items).Title);
    }

    [Fact]
    public void Subscription_rows_parse_like_library_artists()
    {
        var page = LibraryParser.ParseArtists(ParserFixtures.Synthetic("synthetic_library_subscriptions.json"), _log);

        Assert.Equal(["Daft Punk", "Oasis"], page.Items.Select(a => a.Title));
        Assert.Equal(["UCRr1xG_2WIDs18a6cIiCxeA", "UCmMUZbaYdNH0bEd1PAlAqsA"], page.Items.Select(a => a.BrowseId));
        Assert.Equal("7.19M", page.Items[0].Subscribers); // ytmusicapi keeps the first word
        Assert.NotEmpty(page.Items[0].Thumbnails);
        Assert.Equal("SYNTHETIC_SUBSCRIPTIONS_TOKEN", page.Continuation);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public async Task Continuation_of_another_library_page_is_rejected()
    {
        _host.SignIn();
        var token = ContinuationToken.Wrap(
            ContinuationScope.LibraryArtists,
            "T",
            new System.Text.Json.Nodes.JsonObject { ["browseId"] = "FEmusic_library_corpus_track_artists" })!;

        await Assert.ThrowsAsync<ArgumentException>(() => _host.Library.GetSubscriptionsAsync(token, Ct));
        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task A_signed_out_reply_is_not_mistaken_for_an_empty_list()
    {
        _host.SignIn();
        _host.Handler.EnqueueStatus(HttpStatusCode.Unauthorized);

        await Assert.ThrowsAnyAsync<HushException>(() => _host.Library.GetSubscriptionsAsync(null, Ct));
    }
}
