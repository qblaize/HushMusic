using System.Net;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>
/// Adding songs that are already in the playlist (ytmusicapi add_playlist_items(duplicates=...)). Without dedupeOption the
/// server fails the whole request; ytmusicapi documents only that the status is not STATUS_SUCCEEDED.
/// </summary>
public sealed class PlaylistDuplicateTests : IDisposable
{
    private const string Failed = """{"status":"STATUS_FAILED"}""";

    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Refused_add_without_duplicates_is_reported_as_already_in_the_playlist()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Failed);

        var ex = await Assert.ThrowsAsync<AlreadyInPlaylistException>(() => _host.Account.AddPlaylistItemsAsync("VLPL1", ["a1", "b2"], allowDuplicates: false, Ct));

        Assert.Equal("VLPL1", ex.PlaylistId);
        Assert.Equal(["a1", "b2"], ex.VideoIds);
        Assert.Contains("STATUS_FAILED", Assert.IsType<InnerTubeException>(ex.InnerException).Message);

        // Sent once, without dedupeOption, so the server checked for duplicates.
        var request = Assert.Single(_host.Handler.Requests);
        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_ADD_VIDEO","addedVideoId":"a1"},{"action":"ACTION_ADD_VIDEO","addedVideoId":"b2"}]}""",
            request.BodyWithoutContext);
    }

    [Fact]
    public async Task Adding_anyway_skips_the_server_check()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Failed);
        _host.Handler.EnqueueJson(
            """{"status":"STATUS_SUCCEEDED","playlistEditResults":[{"playlistEditVideoAddedResultData":{"videoId":"a1","setVideoId":"NEW_1"}}]}""");

        await Assert.ThrowsAsync<AlreadyInPlaylistException>(() => _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: false, Ct));
        var added = await _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: true, Ct);

        Assert.Equal([new PlaylistEntryRef("a1", "NEW_1")], added);
        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_ADD_VIDEO","addedVideoId":"a1","dedupeOption":"DEDUPE_OPTION_SKIP"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Failed_add_with_duplicates_allowed_is_a_plain_error()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Failed);

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: true, Ct));

        Assert.Contains("STATUS_FAILED", ex.Message);
    }

    [Fact]
    public async Task Gated_add_is_not_taken_for_duplicates()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(
            """{"status":"STATUS_FAILED","actions":[{"showEngagementPanelEndpoint":{"identifier":{"tag":"PAyoutube_music_restriction"}}}]}""");

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: false, Ct));

        Assert.Contains("PAyoutube_music_restriction", ex.Message);
    }

    [Fact]
    public async Task Http_error_is_not_taken_for_duplicates()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"error":{"code":409,"message":"Conflict"}}""", HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: false, Ct));

        Assert.Equal(409, ex.StatusCode);
        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task Response_without_status_still_counts_as_added()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("{}");

        Assert.Empty(await _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], allowDuplicates: false, Ct));
    }
}
