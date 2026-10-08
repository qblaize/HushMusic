using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

public sealed class PlaybackActionsTests
{
    private readonly QueueService _queue = new();
    private readonly FakePlayer _player = new();
    private readonly FakeWatchApi _watch = new();
    private readonly FakeBrowseApi _browse = new();
    private readonly FakeNotifications _notifications = new();
    private readonly PlaybackActions _actions;

    public PlaybackActionsTests()
    {
        _actions = new PlaybackActions(_queue, _player, _watch, _browse, _notifications, NullLogger<PlaybackActions>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task PlayTracks_loads_the_queue_and_plays_the_start_index()
    {
        var source = new QueueSource(QueueSourceKind.Search);

        await _actions.PlayTracksAsync(Tracks("a", "b", "c"), startIndex: 2, source, Ct);

        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
        Assert.Equal(2, _queue.CurrentIndex);
        Assert.Same(source, _queue.Source);
        Assert.Equal([2], _player.PlayedIndices);
    }

    [Fact]
    public async Task PlayTracks_defaults_to_a_manual_source()
    {
        await _actions.PlayTracksAsync(Tracks("a"), cancellationToken: Ct);

        Assert.Equal(QueueSourceKind.Manual, _queue.Source?.Kind);
    }

    [Fact]
    public async Task PlayTracks_skips_unavailable_tracks_and_starts_at_the_next_playable_one()
    {
        Track[] tracks = [Track("a"), Track("x", available: false), Track("b"), Track("y", available: false), Track("c")];

        await _actions.PlayTracksAsync(tracks, startIndex: 1, cancellationToken: Ct);

        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
        Assert.Equal("b", _queue.Current?.Track.VideoId);
        Assert.Equal([1], _player.PlayedIndices);
    }

    [Fact]
    public async Task PlayTracks_with_nothing_playable_throws()
    {
        await Assert.ThrowsAsync<HushException>(() => _actions.PlayTracksAsync([Track("x", available: false)], cancellationToken: Ct));
        Assert.Empty(_player.PlayedIndices);
    }

    [Fact]
    public async Task PlayTrackWithUpNext_plays_the_track_and_appends_up_next_without_the_seed()
    {
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist
        {
            Tracks = Tracks("seed", "n1", "n2"),
            Continuation = "radio-token",
        });

        await _actions.PlayTrackWithUpNextAsync(Track("seed"), Ct);

        Assert.Equal([0], _player.PlayedIndices);
        Assert.Equal(["seed", "n1", "n2"], Ids(_queue.Items));
        Assert.Equal("radio-token", _queue.Continuation);
        Assert.Equal(new QueueSource(QueueSourceKind.UpNext, "seed", "Song seed"), _queue.Source);
        var call = Assert.Single(_watch.Calls);
        Assert.Equal(("seed", null, false, false), (call.VideoId, call.PlaylistId, call.Radio, call.Shuffle));
    }

    [Fact]
    public async Task PlayTrackWithUpNext_starts_playback_before_up_next_arrives()
    {
        var upNext = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = _ => upNext.Task;

        var play = _actions.PlayTrackWithUpNextAsync(Track("seed"), Ct);

        Assert.Equal([0], _player.PlayedIndices);
        Assert.Equal(["seed"], Ids(_queue.Items));
        Assert.False(play.IsCompleted);

        upNext.SetResult(new WatchPlaylist { Tracks = Tracks("seed", "n1") });
        await play;

        Assert.Equal(["seed", "n1"], Ids(_queue.Items));
    }

    [Fact]
    public async Task PlayTrackWithUpNext_keeps_up_next_when_the_seed_is_not_first()
    {
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist { Tracks = Tracks("n1", "n2") });

        await _actions.PlayTrackWithUpNextAsync(Track("seed"), Ct);

        Assert.Equal(["seed", "n1", "n2"], Ids(_queue.Items));
    }

    [Fact]
    public async Task PlayTrackWithUpNext_drops_up_next_when_the_queue_was_replaced_meanwhile()
    {
        var upNext = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = _ => upNext.Task;

        var play = _actions.PlayTrackWithUpNextAsync(Track("seed"), Ct);
        _queue.Load(Tracks("other"));
        upNext.SetResult(new WatchPlaylist { Tracks = Tracks("seed", "n1") });
        await play;

        Assert.Equal(["other"], Ids(_queue.Items));
    }

    [Fact]
    public async Task PlayTrackWithUpNext_reports_but_does_not_throw_when_up_next_fails()
    {
        _watch.OnGet = _ => Task.FromException<WatchPlaylist>(new InnerTubeException("next", "boom"));

        await _actions.PlayTrackWithUpNextAsync(Track("seed"), Ct);

        Assert.Equal(["seed"], Ids(_queue.Items));
        Assert.Equal([0], _player.PlayedIndices);
        var notification = Assert.Single(_notifications.Shown);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
    }

    [Fact]
    public async Task A_newer_play_request_cancels_a_pending_up_next_fill()
    {
        var upNext = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = call =>
        {
            call.Token.Register(() => upNext.TrySetCanceled(call.Token));
            return upNext.Task;
        };

        var first = _actions.PlayTrackWithUpNextAsync(Track("seed"), Ct);
        await _actions.PlayTracksAsync(Tracks("a", "b"), cancellationToken: Ct);
        await first;

        Assert.True(_watch.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(["a", "b"], Ids(_queue.Items));
        Assert.Empty(_notifications.Shown);
    }

    [Fact]
    public async Task PlayTrackWithUpNext_fill_is_not_tied_to_the_callers_token()
    {
        var upNext = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = _ => upNext.Task;
        using var pageCts = new CancellationTokenSource();

        var play = _actions.PlayTrackWithUpNextAsync(Track("seed"), pageCts.Token);
        await pageCts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => play);

        upNext.SetResult(new WatchPlaylist { Tracks = Tracks("seed", "n1") });
        await WaitUntilAsync(() => _queue.Items.Count == 2);

        Assert.False(_watch.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(["seed", "n1"], Ids(_queue.Items));
    }

    [Fact]
    public async Task StartRadio_requests_a_radio_and_marks_the_queue_as_radio()
    {
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist { Tracks = Tracks("seed", "r1"), Continuation = "more" });

        await _actions.StartRadioAsync(Track("seed"), Ct);

        Assert.True(Assert.Single(_watch.Calls).Radio);
        Assert.Equal(QueueSourceKind.Radio, _queue.Source?.Kind);
        Assert.Equal(["seed", "r1"], Ids(_queue.Items));
        Assert.Equal("more", _queue.Continuation);
    }

    [Fact]
    public async Task PlayPlaylist_uses_the_watch_endpoint_and_keeps_the_continuation()
    {
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist
        {
            Tracks = [Track("a"), Track("x", available: false), Track("b")],
            Continuation = "next-page",
        });

        await _actions.PlayPlaylistAsync("PL123", shuffle: true, Ct);

        var call = Assert.Single(_watch.Calls);
        Assert.Equal((null, "PL123", false, true), (call.VideoId, call.PlaylistId, call.Radio, call.Shuffle));
        Assert.Equal(["a", "b"], Ids(_queue.Items));
        Assert.Equal("next-page", _queue.Continuation);
        Assert.Equal(new QueueSource(QueueSourceKind.Playlist, "PL123"), _queue.Source);
        Assert.Equal([0], _player.PlayedIndices);
    }

    [Fact]
    public async Task PlayPlaylist_throws_when_the_playlist_is_empty()
    {
        await Assert.ThrowsAsync<HushException>(() => _actions.PlayPlaylistAsync("PL123", cancellationToken: Ct));
        Assert.Empty(_player.PlayedIndices);
    }

    [Fact]
    public async Task PlayAlbum_with_an_audio_playlist_uses_the_watch_endpoint()
    {
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist { Tracks = Tracks("t1", "t2") });
        var album = new Album { Title = "Album", BrowseId = "MPREb_1", AudioPlaylistId = "OLAK5uy_1" };

        await _actions.PlayAlbumAsync(album, shuffle: false, Ct);

        Assert.Equal("OLAK5uy_1", Assert.Single(_watch.Calls).PlaylistId);
        Assert.Empty(_browse.AlbumCalls);
        Assert.Equal(new QueueSource(QueueSourceKind.Album, "MPREb_1", "Album"), _queue.Source);
        Assert.Equal(["t1", "t2"], Ids(_queue.Items));
    }

    [Fact]
    public async Task PlayAlbum_looks_up_the_audio_playlist_when_the_album_has_none()
    {
        _browse.Albums["MPREb_1"] = new AlbumPage
        {
            Album = new Album { Title = "Album", BrowseId = "MPREb_1", AudioPlaylistId = "OLAK5uy_1" },
        };
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist { Tracks = Tracks("t1") });

        await _actions.PlayAlbumAsync(new Album { Title = "Album", BrowseId = "MPREb_1" }, cancellationToken: Ct);

        Assert.Equal(["MPREb_1"], _browse.AlbumCalls);
        Assert.Equal("OLAK5uy_1", Assert.Single(_watch.Calls).PlaylistId);
    }

    [Fact]
    public async Task PlayAlbum_falls_back_to_the_album_page_tracks_with_album_details()
    {
        var art = new Thumbnail("https://example.test/art.jpg", 544, 544);
        var album = new Album
        {
            Title = "Album",
            BrowseId = "MPREb_1",
            Thumbnails = [art],
            Artists = [new ArtistRef("Artist", "UC1")],
        };
        _browse.Albums["MPREb_1"] = new AlbumPage { Album = album, Tracks = [Track("t1"), Track("x", available: false), Track("t2")] };

        await _actions.PlayAlbumAsync(album, cancellationToken: Ct);

        Assert.Empty(_watch.Calls);
        Assert.Equal(["t1", "t2"], Ids(_queue.Items));
        var first = _queue.Items[0].Track;
        Assert.Equal(art, first.BestThumbnail);
        Assert.Equal(new AlbumRef("Album", "MPREb_1"), first.Album);
        Assert.Equal("Artist", first.ArtistsText);
        Assert.Equal([0], _player.PlayedIndices);
    }

    [Fact]
    public async Task PlayAlbum_shuffle_without_an_audio_playlist_shuffles_locally()
    {
        var album = new Album { Title = "Album", BrowseId = "MPREb_1" };
        _browse.Albums["MPREb_1"] = new AlbumPage { Album = album, Tracks = Tracks("t1", "t2", "t3", "t4") };

        await _actions.PlayAlbumAsync(album, shuffle: true, Ct);

        Assert.True(_queue.IsShuffled);
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Equal([0], _player.PlayedIndices);
    }

    [Fact]
    public void AddToQueue_and_PlayNext_skip_unavailable_tracks()
    {
        _queue.Load(Tracks("a", "b"));

        _actions.AddToQueue([Track("end"), Track("x", available: false)]);
        _actions.PlayNext([Track("next"), Track("y", available: false)]);

        Assert.Equal(["a", "next", "b", "end"], Ids(_queue.Items));
        Assert.Empty(_player.PlayedIndices);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
    }
}
