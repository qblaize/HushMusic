using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.InnerTube.Api;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>Reordering playlist entries and the add response (ytmusicapi edit_playlist(moveItem=...), add_playlist_items).</summary>
public sealed class PlaylistEditRequestTests : IDisposable
{
    private readonly InnerTubeTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Move_before_a_successor_matches_ytmusicapi()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);

        await _host.Account.MovePlaylistItemAsync("VLPL1", "SET_B", "SET_A", Ct);

        Assert.Equal("/youtubei/v1/browse/edit_playlist", _host.Handler.Last.Uri.AbsolutePath);
        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_MOVE_VIDEO_BEFORE","setVideoId":"SET_B","movedSetVideoIdSuccessor":"SET_A"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Move_without_successor_sends_only_the_entry(string? successor)
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);

        await _host.Account.MovePlaylistItemAsync("PL1", "SET_B", successor, Ct);

        Assert.Equal(
            """{"playlistId":"PL1","actions":[{"action":"ACTION_MOVE_VIDEO_BEFORE","setVideoId":"SET_B"}]}""",
            _host.Handler.Last.BodyWithoutContext);
    }

    [Fact]
    public async Task Move_requires_sign_in_and_an_entry_id()
    {
        await Assert.ThrowsAsync<AuthRequiredException>(() => _host.Account.MovePlaylistItemAsync("PL1", "SET_B", "SET_A", Ct));

        _host.SignIn();
        await Assert.ThrowsAsync<ArgumentException>(() => _host.Account.MovePlaylistItemAsync("PL1", " ", "SET_A", Ct));

        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public async Task Move_failure_status_throws()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"status":"STATUS_FAILED"}""");

        var ex = await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.MovePlaylistItemAsync("PL1", "SET_B", "SET_A", Ct));

        Assert.Contains("STATUS_FAILED", ex.Message);
    }

    [Fact]
    public async Task Gated_move_is_reported_as_not_performed()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson("""{"actions":[{"showEngagementPanelEndpoint":{"identifier":{"tag":"PAyoutube_music_restriction"}}}]}""");

        await Assert.ThrowsAsync<InnerTubeException>(() => _host.Account.MovePlaylistItemAsync("PL1", "SET_B", null, Ct));
    }

    [Fact]
    public async Task Add_returns_the_new_entry_ids_in_order()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(
            """
            {"status":"STATUS_SUCCEEDED","playlistEditResults":[
              {"playlistEditVideoAddedResultData":{"videoId":"a1","setVideoId":"NEW_1"}},
              {"playlistEditVideoAddedResultData":{"videoId":"b2","setVideoId":"NEW_2"}},
              {"playlistEditVideoAddedResultData":{"videoId":"a1","setVideoId":"NEW_3"}}]}
            """);

        var added = await _host.Account.AddPlaylistItemsAsync("PL1", ["a1", "b2", "a1"], allowDuplicates: true, Ct);

        Assert.Equal(
            new[] { new PlaylistEntryRef("a1", "NEW_1"), new PlaylistEntryRef("b2", "NEW_2"), new PlaylistEntryRef("a1", "NEW_3") },
            added);
    }

    [Fact]
    public async Task Add_without_results_or_with_incomplete_ones_returns_what_is_known()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Responses.Succeeded);
        _host.Handler.EnqueueJson(
            """{"status":"STATUS_SUCCEEDED","playlistEditResults":[{},{"playlistEditVideoAddedResultData":{"videoId":"b2"}},{"playlistEditVideoAddedResultData":{"videoId":"c3","setVideoId":"NEW_3"}}]}""");

        Assert.Empty(await _host.Account.AddPlaylistItemsAsync("PL1", ["a1"], false, Ct));
        var partial = await _host.Account.AddPlaylistItemsAsync("PL1", ["a1", "b2", "c3"], false, Ct);
        Assert.Equal(new[] { new PlaylistEntryRef("c3", "NEW_3") }, partial);
    }

    [Fact]
    public void Added_entries_parser_ignores_unexpected_shapes()
    {
        Assert.Empty(AccountApi.ParseAddedEntries(System.Text.Json.Nodes.JsonNode.Parse("""{"playlistEditResults":{"x":1}}""")!));
        Assert.Empty(AccountApi.ParseAddedEntries(System.Text.Json.Nodes.JsonNode.Parse("""{"playlistEditResults":[null,1,"x"]}""")!));
    }
}
