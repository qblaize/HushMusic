using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

public sealed class QueueAutoplayTests : IAsyncLifetime
{
    private readonly QueueService _queue = new();
    private readonly FakePlayer _player = new();
    private readonly FakeWatchApi _watch = new();
    private readonly AutoplayFakeSettings _settings = new();
    private readonly QueueAutoplay _autoplay;

    public QueueAutoplayTests()
    {
        _autoplay = new QueueAutoplay(_queue, _player, _watch, _settings, NullLogger<QueueAutoplay>.Instance);

        // A radio seeded by X starts with X itself, like the real watch endpoint.
        _watch.OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = Tracks(call.VideoId!, "s1", "s2", "s3", "s4", "s5") });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => await _autoplay.StartAsync(Ct);

    public async ValueTask DisposeAsync()
    {
        await _autoplay.StopAsync(CancellationToken.None);
        _autoplay.Dispose();
    }

    [Fact]
    public void Appends_similar_songs_when_the_last_track_of_an_album_starts()
    {
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album, "MPREb_1", "Album"), "a", "b");

        var call = Assert.Single(_watch.Calls);
        Assert.Equal("b", call.VideoId);
        Assert.True(call.Radio);
        Assert.Equal(["a", "b", "s1", "s2", "s3", "s4", "s5"], Ids(_queue.Items));
        Assert.Equal(Ids(_queue.Items.Skip(2)), Ids(_queue.Items.Where(i => _autoplay.SuggestedItemIds.Contains(i.Id))));
        Assert.Equal("b", _autoplay.Seed?.VideoId);
    }

    [Fact]
    public void Skips_songs_that_are_already_queued_or_unavailable()
    {
        _watch.OnGet = _ => Task.FromResult(new WatchPlaylist { Tracks = [Track("b"), Track("a"), Track("s1", available: false), Track("s2"), Track("s2")] });

        LoadAndStartLast(new QueueSource(QueueSourceKind.Playlist, "PL1"), "a", "b");

        Assert.Equal(["a", "b", "s2"], Ids(_queue.Items));
    }

    [Fact]
    public void Waits_until_the_last_track_actually_starts()
    {
        _queue.Load(Tracks("a", "b"), 0, new QueueSource(QueueSourceKind.Album));
        _player.RaiseTrackStarted(_queue.Items[0]);
        _queue.MoveNext(userInitiated: false);

        Assert.Empty(_watch.Calls);

        _player.RaiseTrackStarted(_queue.Items[1]);

        Assert.Single(_watch.Calls);
    }

    [Fact]
    public void Leaves_queues_that_extend_themselves_alone()
    {
        _queue.Load(Tracks("a"), 0, new QueueSource(QueueSourceKind.Radio, "x"), continuation: "radio-page-2");
        _player.RaiseTrackStarted(_queue.Items[0]);

        Assert.Empty(_watch.Calls);
    }

    [Fact]
    public void Leaves_live_radio_alone()
    {
        var station = new RadioStation { Id = "st1", Name = "Station", StreamUrl = "https://example.com/stream" };
        _queue.Load([station.ToTrack()], 0, new QueueSource(QueueSourceKind.LiveRadio, "genre", "Genre radio"));
        _player.RaiseTrackStarted(_queue.Items[0]);

        Assert.Empty(_watch.Calls);
    }

    [Fact]
    public void Waits_for_up_next_after_playing_a_single_song()
    {
        _queue.Load(Tracks("a"), 0, new QueueSource(QueueSourceKind.UpNext, "a", "Song a"));
        _player.RaiseTrackStarted(_queue.Items[0]);

        Assert.Empty(_watch.Calls);

        // Up next arrived without a continuation: a finite queue like any other.
        _queue.AppendContinuation(Tracks("b"), null);
        _queue.MoveNext(userInitiated: false);
        _player.RaiseTrackStarted(_queue.Items[1]);

        Assert.Equal("b", Assert.Single(_watch.Calls).VideoId);
    }

    [Theory]
    [InlineData(RepeatMode.All)]
    [InlineData(RepeatMode.One)]
    public void Does_nothing_while_repeat_is_on(RepeatMode repeat)
    {
        _queue.RepeatMode = repeat;

        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        Assert.Empty(_watch.Calls);
    }

    [Fact]
    public void Follows_the_setting_live()
    {
        _settings.Current.AutoplayWhenQueueEnds = false;
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        Assert.Empty(_watch.Calls);

        _settings.Change(s => s.AutoplayWhenQueueEnds = true);

        Assert.Single(_watch.Calls);
        Assert.Equal(7, _queue.Items.Count);
    }

    [Fact]
    public void Turning_the_setting_off_removes_the_suggestions_still_to_come()
    {
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");
        _queue.MoveNext(userInitiated: false); // now playing s1, a suggestion

        _settings.Change(s => s.AutoplayWhenQueueEnds = false);

        Assert.Equal(["a", "b", "s1"], Ids(_queue.Items));
        Assert.Equal("s1", _queue.Current?.Track.VideoId);
        Assert.Single(_autoplay.SuggestedItemIds);
    }

    [Fact]
    public void Stays_quiet_after_the_user_clears_up_next_until_songs_are_added_again()
    {
        _queue.Load(Tracks("a", "b", "c"), 0, new QueueSource(QueueSourceKind.Album));
        _queue.RemoveAt(2);
        _queue.RemoveAt(1);
        _player.RaiseTrackStarted(_queue.Items[0]);

        Assert.Empty(_watch.Calls);

        _queue.Enqueue(Tracks("d"));
        _queue.MoveNext(userInitiated: false);
        _player.RaiseTrackStarted(_queue.Items[1]);

        Assert.Equal("d", Assert.Single(_watch.Calls).VideoId);
    }

    [Fact]
    public void Clearing_up_next_also_stops_the_suggestions()
    {
        _watch.OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = Tracks(call.VideoId!, "s1", "s2"), Continuation = "r1" });
        _watch.OnContinuation = (_, _) => Task.FromResult(new Paged<Track>(Tracks("s3", "s4", "s5"), "r2"));
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");
        Assert.Equal(7, _queue.Items.Count);

        // What "Clear" in Up next does: remove from the end down to the current song.
        for (var last = _queue.Items.Count - 1; last > _queue.CurrentIndex; last--)
        {
            _queue.RemoveAt(last);
        }

        _player.RaiseTrackStarted(_queue.Items[^1]);

        Assert.Equal(["a", "b"], Ids(_queue.Items));
        Assert.Equal(["r1"], _watch.ContinuationCalls);
        Assert.Single(_watch.Calls);
        Assert.Empty(_autoplay.SuggestedItemIds);
    }

    [Fact]
    public void Keeps_extending_with_the_radio_continuation()
    {
        _watch.OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = Tracks(call.VideoId!, "s1", "s2"), Continuation = "r1" });
        _watch.OnContinuation = (token, _) => Task.FromResult(token switch
        {
            "r1" => new Paged<Track>(Tracks("s3", "s4", "s5"), "r2"),
            _ => throw new InvalidOperationException("should not be requested yet"),
        });

        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        Assert.Equal(["r1"], _watch.ContinuationCalls);
        Assert.Equal(["a", "b", "s1", "s2", "s3", "s4", "s5"], Ids(_queue.Items));
        Assert.Equal(5, _autoplay.SuggestedItemIds.Count);

        // The queue itself stays finite: the radio belongs to autoplay.
        Assert.Null(_queue.Continuation);
    }

    [Fact]
    public async Task Drops_the_songs_when_the_queue_was_replaced_meanwhile()
    {
        var radio = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = _ => radio.Task;
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        _queue.Load(Tracks("x", "y", "z"), 0, new QueueSource(QueueSourceKind.Playlist));
        radio.SetResult(new WatchPlaylist { Tracks = Tracks("b", "s1") });
        await Task.Delay(50, Ct);

        Assert.Equal(["x", "y", "z"], Ids(_queue.Items));
        Assert.Empty(_autoplay.SuggestedItemIds);
    }

    [Fact]
    public async Task Drops_the_songs_when_the_user_added_some_meanwhile()
    {
        var radio = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = _ => radio.Task;
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        _queue.Enqueue(Tracks("c"));
        radio.SetResult(new WatchPlaylist { Tracks = Tracks("b", "s1") });
        await Task.Delay(50, Ct);

        Assert.Equal(["a", "b", "c"], Ids(_queue.Items));
    }

    [Fact]
    public async Task Carries_on_when_the_songs_arrive_after_the_queue_ended()
    {
        var radio = new TaskCompletionSource<WatchPlaylist>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnGet = _ => radio.Task;
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");
        _player.RaiseStatusChanged(PlaybackStatus.Ended);

        radio.SetResult(new WatchPlaylist { Tracks = Tracks("b", "s1") });
        await WaitUntilAsync(() => _player.PlayedIndices.Count > 0);

        Assert.Equal([2], _player.PlayedIndices);
    }

    [Fact]
    public void Retries_a_failed_request_only_after_a_while()
    {
        _watch.OnGet = _ => Task.FromException<WatchPlaylist>(new InnerTubeException("next", "boom"));
        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        _player.RaiseTrackStarted(_queue.Items[1]);

        Assert.Single(_watch.Calls);
    }

    [Fact]
    public async Task Stops_listening_after_StopAsync()
    {
        await _autoplay.StopAsync(Ct);

        LoadAndStartLast(new QueueSource(QueueSourceKind.Album), "a", "b");

        Assert.Empty(_watch.Calls);
    }

    private void LoadAndStartLast(QueueSource source, params string[] ids)
    {
        _queue.Load(Tracks(ids), ids.Length - 1, source);
        _player.RaiseTrackStarted(_queue.Items[^1]);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }
    }
}

public sealed class AddToQueueWithSuggestionsTests
{
    private readonly QueueService _queue = new();
    private readonly AutoplayFakeSuggestions _suggestions = new();
    private readonly PlaybackActions _actions;

    public AddToQueueWithSuggestionsTests()
    {
        _actions = new PlaybackActions(_queue, new FakePlayer(), new FakeWatchApi(), new FakeBrowseApi(), new FakeNotifications(), NullLogger<PlaybackActions>.Instance, _suggestions);
    }

    [Fact]
    public void Added_songs_play_before_the_suggestions_still_to_come()
    {
        _queue.Load(Tracks("a", "b", "s1", "s2", "s3"), 2);
        _suggestions.Mark(_queue.Items.Skip(2));

        _actions.AddToQueue(Tracks("x", "y"));

        Assert.Equal(["a", "b", "s1", "x", "y", "s2", "s3"], Ids(_queue.Items));
    }

    [Fact]
    public void Without_suggestions_songs_go_to_the_end()
    {
        _queue.Load(Tracks("a", "b"));

        _actions.AddToQueue(Tracks("x"));

        Assert.Equal(["a", "b", "x"], Ids(_queue.Items));
    }

    [Fact]
    public void Play_next_is_unchanged()
    {
        _queue.Load(Tracks("a", "s1"));
        _suggestions.Mark(_queue.Items.Skip(1));

        _actions.PlayNext(Tracks("x"));

        Assert.Equal(["a", "x", "s1"], Ids(_queue.Items));
    }
}

/// <summary>Settings whose <see cref="ISettingsService.Changed"/> fires, for features that react to a setting live.</summary>
internal sealed class AutoplayFakeSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        Change(update);
        return Task.CompletedTask;
    }

    public void Change(Action<AppSettings> update)
    {
        update(Current);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}

internal sealed class AutoplayFakeSuggestions : IQueueAutoplay
{
    private HashSet<Guid> _ids = [];

    public event EventHandler? Changed;

    public IReadOnlySet<Guid> SuggestedItemIds => _ids;

    public Track? Seed => null;

    public void Mark(IEnumerable<QueueItem> items)
    {
        _ids = [.. items.Select(i => i.Id)];
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
