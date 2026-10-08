using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using HushMusic.Core.Services;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

internal static class Eventually
{
    public static async Task TrueAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met within 5 s");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}

public sealed class PlaybackGainTests
{
    [Theory]
    [InlineData(null, 1.0)]
    [InlineData(-7.5, 1.0)]
    [InlineData(0.0, 1.0)]
    [InlineData(6.0206, 0.5)]
    [InlineData(20.0, 0.1)]
    [InlineData(3.0, 0.7079)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    public void Loudness_only_ever_turns_loud_tracks_down(double? loudnessDb, double expected) =>
        Assert.Equal(expected, PlaybackGain.ForLoudness(loudnessDb), 4);

    [Fact]
    public void Combine_multiplies_and_clamps()
    {
        Assert.Equal(0.4, PlaybackGain.Combine(0.8, 0.5, 1), 10);
        Assert.Equal(0.2, PlaybackGain.Combine(0.8, 0.5, 0.5), 10);
        Assert.Equal(1, PlaybackGain.Combine(1.5, 1, 1));
        Assert.Equal(0, PlaybackGain.Combine(0.8, 0.5, 0));
        Assert.Equal(0, PlaybackGain.Combine(double.NaN, 1, 1));
    }

    [Fact]
    public void Linear_ramp_interpolates_evenly()
    {
        Assert.Equal(0.5, PlaybackGain.Interpolate(0.5, 1, 0, RampCurve.Linear), 10);
        Assert.Equal(0.75, PlaybackGain.Interpolate(0.5, 1, 0.5, RampCurve.Linear), 10);
        Assert.Equal(1, PlaybackGain.Interpolate(0.5, 1, 1, RampCurve.Linear), 10);
        Assert.Equal(1, PlaybackGain.Interpolate(0.5, 1, 7, RampCurve.Linear), 10);
    }

    [Fact]
    public void Fade_out_follows_a_squared_curve_to_silence()
    {
        Assert.Equal(1, PlaybackGain.Interpolate(1, 0, 0, RampCurve.FadeOut), 10);
        Assert.Equal(0.5625, PlaybackGain.Interpolate(1, 0, 0.25, RampCurve.FadeOut), 10);
        Assert.Equal(0.25, PlaybackGain.Interpolate(1, 0, 0.5, RampCurve.FadeOut), 10);
        Assert.Equal(0, PlaybackGain.Interpolate(1, 0, 1, RampCurve.FadeOut), 10);
    }

    [Fact]
    public void Ramp_runs_from_start_to_target_and_restarts_from_its_current_value()
    {
        var ramp = new GainRamp(1, 0.5, StartMs: 1000, DurationMs: 150, RampCurve.Linear);

        Assert.Equal(1, ramp.ValueAt(900));
        Assert.Equal(0.75, ramp.ValueAt(1075), 10);
        Assert.Equal(0.5, ramp.ValueAt(1150));
        Assert.False(ramp.IsDoneAt(1149));
        Assert.True(ramp.IsDoneAt(1150));

        var back = ramp.RampTo(1, TimeSpan.FromMilliseconds(100), RampCurve.Linear, nowMs: 1075);
        Assert.Equal(0.75, back.ValueAt(1075), 10);
        Assert.Equal(1, back.ValueAt(1175));

        Assert.True(GainRamp.Fixed(0.3).IsDoneAt(0));
        Assert.Equal(0.3, GainRamp.Fixed(0.3).ValueAt(12345));
    }
}

public sealed class VolumeNormalizerTests
{
    private readonly FakeWatchApi _watch = new();
    private readonly FakeSettings _settings = new();
    private readonly VolumeNormalizer _normalizer;

    public VolumeNormalizerTests()
    {
        _normalizer = new VolumeNormalizer(_watch, _settings, NullLogger<VolumeNormalizer>.Instance);
    }

    [Fact]
    public async Task Fetches_each_track_once_and_caches_it()
    {
        _watch.OnLoudness = (id, _) => Task.FromResult<double?>(id == "loud" ? 6.0 : -3.0);

        Assert.False(_normalizer.TryGetLoudness("loud", out _));
        Assert.Equal(6.0, await _normalizer.GetLoudnessDbAsync("loud", TestContext.Current.CancellationToken));
        Assert.Equal(6.0, await _normalizer.GetLoudnessDbAsync("loud", TestContext.Current.CancellationToken));
        Assert.True(_normalizer.TryGetLoudness("loud", out var cached));
        Assert.Equal(6.0, cached);

        _normalizer.Prefetch("quiet");
        await Eventually.TrueAsync(() => _normalizer.TryGetLoudness("quiet", out _));

        Assert.Equal(["loud", "quiet"], _watch.LoudnessCalls);
    }

    [Fact]
    public async Task Concurrent_requests_share_one_fetch()
    {
        var response = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnLoudness = (_, _) => response.Task;

        var first = _normalizer.GetLoudnessDbAsync("a", TestContext.Current.CancellationToken);
        var second = _normalizer.GetLoudnessDbAsync("a", TestContext.Current.CancellationToken);
        response.SetResult(4.0);

        Assert.Equal(4.0, await first);
        Assert.Equal(4.0, await second);
        Assert.Single(_watch.LoudnessCalls);
    }

    [Fact]
    public async Task Failures_return_unknown_and_are_retried_later()
    {
        var calls = 0;
        _watch.OnLoudness = (_, _) => ++calls == 1
            ? Task.FromException<double?>(new HttpRequestException("offline"))
            : Task.FromResult<double?>(2.0);

        Assert.Null(await _normalizer.GetLoudnessDbAsync("a", TestContext.Current.CancellationToken));
        Assert.False(_normalizer.TryGetLoudness("a", out _));
        Assert.Equal(2.0, await _normalizer.GetLoudnessDbAsync("a", TestContext.Current.CancellationToken));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task A_cancelled_waiter_does_not_cancel_the_shared_fetch()
    {
        var response = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _watch.OnLoudness = (_, _) => response.Task;
        using var cts = new CancellationTokenSource();

        var waiting = _normalizer.GetLoudnessDbAsync("a", cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);

        response.SetResult(1.5);
        Assert.Equal(1.5, await _normalizer.GetLoudnessDbAsync("a", TestContext.Current.CancellationToken));
        Assert.Single(_watch.LoudnessCalls);
    }

    [Fact]
    public void Follows_the_setting()
    {
        Assert.True(_normalizer.IsEnabled);
        _settings.Current.NormalizeVolume = false;
        Assert.False(_normalizer.IsEnabled);
    }
}

public sealed class QueueSnapshotTests
{
    private readonly QueueService _queue = new();

    [Fact]
    public void Restore_brings_back_items_ids_cursor_and_queue_details()
    {
        var source = new QueueSource(QueueSourceKind.Radio, "abc", "Radio");
        _queue.Load(Tracks("a", "b", "c"), 1, source, continuation: "next-page");
        _queue.RepeatMode = RepeatMode.All;
        var saved = _queue.GetSnapshot();

        var restored = new QueueService();
        var changes = new List<QueueChangeKind>();
        QueueCurrentChangedEventArgs? current = null;
        restored.Changed += (_, e) => changes.Add(e.Kind);
        restored.CurrentChanged += (_, e) => current = e;

        restored.Restore(saved);

        Assert.Equal(saved.Items, restored.Items);
        Assert.Equal(1, restored.CurrentIndex);
        Assert.Equal(source, restored.Source);
        Assert.Equal("next-page", restored.Continuation);
        Assert.Equal(RepeatMode.All, restored.RepeatMode);
        Assert.False(restored.IsShuffled);
        Assert.Equal([QueueChangeKind.Reset], changes);
        Assert.Equal(saved.Items[1].Id, current?.Current?.Id);
    }

    [Fact]
    public void Restore_keeps_shuffle_and_its_original_order()
    {
        _queue.Load(Tracks("a", "b", "c", "d", "e"), 2);
        _queue.SetShuffle(true);
        var saved = _queue.GetSnapshot();
        Assert.NotNull(saved.UnshuffledItems);

        var restored = new QueueService();
        restored.Restore(saved);

        Assert.True(restored.IsShuffled);
        Assert.Equal(Ids(saved.Items), Ids(restored.Items));
        Assert.Equal("c", restored.Current?.Track.VideoId);

        restored.SetShuffle(false);
        Assert.Equal(["a", "b", "c", "d", "e"], Ids(restored.Items));
        Assert.Equal("c", restored.Current?.Track.VideoId);
    }

    [Fact]
    public void Restore_repairs_duplicate_ids_and_clamps_the_cursor()
    {
        var item = QueueItem.Create(Track("a"));
        var snapshot = new QueueSnapshot([item, item with { Track = Track("b") }], CurrentIndex: 9, UnshuffledItems: [item]);

        _queue.Restore(snapshot);

        Assert.Equal(["a", "b"], Ids(_queue.Items));
        Assert.Equal(2, _queue.Items.Select(i => i.Id).Distinct().Count());
        Assert.Equal(1, _queue.CurrentIndex);
        Assert.False(_queue.IsShuffled);
    }

    [Fact]
    public void Restoring_an_empty_snapshot_empties_the_queue()
    {
        _queue.Load(Tracks("a"));
        _queue.Restore(new QueueSnapshot([], 0));

        Assert.Empty(_queue.Items);
        Assert.Equal(-1, _queue.CurrentIndex);
    }
}

public sealed class SleepTimerTests : IDisposable
{
    private readonly ManualTimeProvider _time = new();
    private readonly FakePlayer _player = new() { Status = PlaybackStatus.Playing };
    private readonly SleepTimer _timer;
    private int _changes;

    public SleepTimerTests()
    {
        _timer = new SleepTimer(_player, _time, NullLogger<SleepTimer>.Instance);
        _timer.Changed += (_, _) => Interlocked.Increment(ref _changes);
    }

    private int Changes => Volatile.Read(ref _changes);

    public void Dispose() => _timer.Dispose();

    [Fact]
    public async Task Fades_out_at_expiry_then_pauses_and_turns_off()
    {
        var start = _time.GetUtcNow();
        _timer.Start(TimeSpan.FromMinutes(10));

        Assert.True(_timer.IsActive);
        Assert.False(_timer.StopsAtEndOfTrack);
        Assert.Equal(start + TimeSpan.FromMinutes(10), _timer.EndsAt);
        Assert.Equal(1, Changes);

        _time.Advance(TimeSpan.FromMinutes(9));
        Assert.Empty(_player.Fades);

        _time.Advance(TimeSpan.FromMinutes(1));
        var fade = Assert.Single(_player.Fades);
        Assert.Equal(SleepTimer.FadeDuration, fade.Duration);
        Assert.True(_timer.IsActive); // still cancellable during the fade
        Assert.Equal(0, _player.PauseCount);

        fade.Completion.SetResult();
        await Eventually.TrueAsync(() => Changes == 2);

        Assert.False(_timer.IsActive);
        Assert.Null(_timer.EndsAt);
        Assert.Equal(1, _player.PauseCount);
        Assert.Equal(0, _player.Volume); // the user's volume was never touched
    }

    [Fact]
    public void Cancel_before_expiry_never_fades()
    {
        _timer.Start(TimeSpan.FromMinutes(5));
        _timer.Cancel();
        _time.Advance(TimeSpan.FromMinutes(10));

        Assert.False(_timer.IsActive);
        Assert.Empty(_player.Fades);
        Assert.Equal(2, Changes);

        _timer.Cancel(); // already off: no event
        Assert.Equal(2, Changes);
    }

    [Fact]
    public async Task Cancel_during_the_fade_stops_it_without_pausing()
    {
        _timer.Start(TimeSpan.FromMinutes(1));
        _time.Advance(TimeSpan.FromMinutes(1));
        var fade = Assert.Single(_player.Fades);

        _timer.Cancel();

        Assert.True(fade.Token.IsCancellationRequested);
        Assert.False(_timer.IsActive);
        Assert.Equal(2, Changes);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(0, _player.PauseCount);
        Assert.Equal(2, Changes);
    }

    [Fact]
    public void Start_replaces_the_running_timer()
    {
        _timer.Start(TimeSpan.FromMinutes(30));
        _timer.Start(TimeSpan.FromMinutes(5));

        _time.Advance(TimeSpan.FromMinutes(5));
        Assert.Single(_player.Fades);

        _time.Advance(TimeSpan.FromMinutes(30));
        Assert.Single(_player.Fades);
    }

    [Fact]
    public void End_of_track_arms_the_player_and_turns_off_when_the_track_ends()
    {
        _timer.Start(TimeSpan.FromMinutes(5));
        _timer.StopAtEndOfTrack();

        Assert.True(_timer.IsActive);
        Assert.True(_timer.StopsAtEndOfTrack);
        Assert.Null(_timer.EndsAt);
        Assert.True(_player.PauseAtEndOfTrack);

        _time.Advance(TimeSpan.FromMinutes(10));
        Assert.Empty(_player.Fades); // the timed timer was replaced

        _player.RaiseTrackCompleted(Track("a"));

        Assert.False(_timer.IsActive);
        Assert.False(_timer.StopsAtEndOfTrack);
        Assert.Equal(3, Changes);
    }

    [Fact]
    public void End_of_track_armed_after_a_track_ended_waits_for_the_next_end()
    {
        _timer.StopAtEndOfTrack();

        // The player had already finished this end without the flag, so it is still set.
        _player.RaiseTrackCompleted(Track("a"), consumePauseAtEnd: false);
        Assert.True(_timer.IsActive);

        _player.RaiseTrackCompleted(Track("b"));
        Assert.False(_timer.IsActive);
    }

    [Fact]
    public void Cancel_and_start_disarm_the_end_of_track_flag()
    {
        _timer.StopAtEndOfTrack();
        _timer.Cancel();
        Assert.False(_player.PauseAtEndOfTrack);

        _timer.StopAtEndOfTrack();
        _timer.Start(TimeSpan.FromMinutes(1));
        Assert.False(_player.PauseAtEndOfTrack);
        Assert.False(_timer.StopsAtEndOfTrack);
    }
}

public sealed class PlaybackSessionKeeperTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;
    private readonly ManualTimeProvider _time = new();
    private readonly FakeSettings _settings = new();

    public PlaybackSessionKeeperTests()
    {
        _paths = new AppPaths(_root);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string SessionFile => Path.Combine(_paths.Cache, PlaybackSessionKeeper.FileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Saves_on_shutdown_and_restores_paused_at_the_saved_position()
    {
        var (queue, player, keeper) = Create();
        await keeper.StartAsync(Ct);
        var track = Track("b") with
        {
            Artists = [new ArtistRef("Artist", "UC1")],
            Album = new AlbumRef("Album", "MPREb_1"),
            Duration = TimeSpan.FromMinutes(4),
            Thumbnails = [new Thumbnail("https://img/1.jpg", 120, 120)],
        };
        queue.Load([Track("a"), track, Track("c")], 1, new QueueSource(QueueSourceKind.Radio, "a", "Radio"), "radio-token");
        queue.RepeatMode = RepeatMode.All;
        queue.SetShuffle(true);
        player.CurrentTrack = track;
        player.Status = PlaybackStatus.Paused;
        player.Position = TimeSpan.FromSeconds(83);
        var saved = queue.GetSnapshot();

        await keeper.StopAsync(Ct);
        keeper.Dispose();
        Assert.True(File.Exists(SessionFile));

        var (queue2, player2, keeper2) = Create();
        await keeper2.StartAsync(Ct);

        // Track lists come back as new list instances, so compare them field by field rather than by record equality.
        Assert.Equal(saved.Items.Select(i => i.Id), queue2.Items.Select(i => i.Id));
        Assert.Equal(Ids(saved.Items), Ids(queue2.Items));
        Assert.Equal(saved.CurrentIndex, queue2.CurrentIndex);
        var restored = queue2.Current!.Track;
        Assert.Equal(track.VideoId, restored.VideoId);
        Assert.Equal(track.Title, restored.Title);
        Assert.Equal(track.Artists, restored.Artists);
        Assert.Equal(track.Album, restored.Album);
        Assert.Equal(track.Duration, restored.Duration);
        Assert.Equal(track.Thumbnails, restored.Thumbnails);
        Assert.Equal(new QueueSource(QueueSourceKind.Radio, "a", "Radio"), queue2.Source);
        Assert.Equal("radio-token", queue2.Continuation);
        Assert.Equal(RepeatMode.All, queue2.RepeatMode);
        Assert.True(queue2.IsShuffled);
        Assert.Equal([TimeSpan.FromSeconds(83)], player2.Seeks);
        Assert.Empty(player2.PlayedIndices); // restored, not played

        queue2.SetShuffle(false);
        Assert.Equal(["a", "b", "c"], Ids(queue2.Items));
        await keeper2.StopAsync(Ct);
    }

    [Fact]
    public async Task Saves_when_paused_and_every_interval_while_playing()
    {
        var (queue, player, keeper) = Create();
        await keeper.StartAsync(Ct);
        queue.Load(Tracks("a", "b"));
        player.CurrentTrack = queue.Current!.Track;
        player.Status = PlaybackStatus.Playing;

        player.RaisePositionChanged(TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.Zero);
        Assert.False(File.Exists(SessionFile)); // the queue change is still debounced, no interval save yet

        _time.Advance(TimeSpan.FromSeconds(1));
        await Eventually.TrueAsync(() => SavedPosition() == TimeSpan.FromSeconds(5));

        _time.Advance(PlaybackSessionKeeper.SaveInterval);
        player.RaisePositionChanged(TimeSpan.FromSeconds(20));
        _time.Advance(TimeSpan.Zero);
        await Eventually.TrueAsync(() => SavedPosition() == TimeSpan.FromSeconds(20));

        player.Position = TimeSpan.FromSeconds(21);
        player.RaiseStatusChanged(PlaybackStatus.Paused);
        _time.Advance(TimeSpan.Zero);
        await Eventually.TrueAsync(() => SavedPosition() == TimeSpan.FromSeconds(21));

        await keeper.StopAsync(Ct);
    }

    [Fact]
    public async Task An_ended_queue_is_saved_at_the_start_of_its_track()
    {
        var (queue, player, keeper) = Create();
        await keeper.StartAsync(Ct);
        queue.Load(Tracks("a"));
        player.CurrentTrack = queue.Current!.Track;
        player.Status = PlaybackStatus.Ended;
        player.Position = TimeSpan.FromMinutes(3);
        await keeper.StopAsync(Ct);

        var (_, player2, keeper2) = Create();
        await keeper2.StartAsync(Ct);
        Assert.Empty(player2.Seeks);
        await keeper2.StopAsync(Ct);
    }

    [Fact]
    public async Task Does_not_restore_a_session_older_than_30_days()
    {
        await SaveOneTrackSessionAsync();
        _time.Advance(PlaybackSessionKeeper.MaxAge + TimeSpan.FromMinutes(1));

        var (queue, _, keeper) = Create();
        await keeper.StartAsync(Ct);

        Assert.Empty(queue.Items);
        Assert.False(File.Exists(SessionFile));
        await keeper.StopAsync(Ct);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{"Version":1,"SavedAt":"2026-10-07T12:00:00+00:00","Items":[]}""")]
    [InlineData("""{"Version":99,"SavedAt":"2026-10-07T12:00:00+00:00","Items":[{"Id":"7d1f2b3c-0000-0000-0000-000000000001","Track":{"Title":"x","VideoId":"x"}}]}""")]
    [InlineData("""{"Version":1,"SavedAt":"2026-10-07T12:00:00+00:00","Items":[{"Id":"7d1f2b3c-0000-0000-0000-000000000001","Track":{"Title":"x"}}]}""")]
    public async Task Ignores_a_corrupt_or_invalid_file(string content)
    {
        await File.WriteAllTextAsync(SessionFile, content, Ct);

        var (queue, player, keeper) = Create();
        await keeper.StartAsync(Ct);

        Assert.Empty(queue.Items);
        Assert.Empty(player.Seeks);
        Assert.False(File.Exists(SessionFile));
        await keeper.StopAsync(Ct);
    }

    [Fact]
    public async Task Turned_off_it_neither_restores_nor_keeps_a_session()
    {
        await SaveOneTrackSessionAsync();
        _settings.Current.ResumeLastSession = false;

        var (queue, _, keeper) = Create();
        await keeper.StartAsync(Ct);
        Assert.Empty(queue.Items);
        Assert.False(File.Exists(SessionFile));

        queue.Load(Tracks("z"));
        await keeper.StopAsync(Ct);
        Assert.False(File.Exists(SessionFile));
    }

    [Fact]
    public async Task Does_not_replace_a_queue_that_is_already_loaded()
    {
        await SaveOneTrackSessionAsync();

        var (queue, _, keeper) = Create();
        queue.Load(Tracks("mine"));
        await keeper.StartAsync(Ct);

        Assert.Equal(["mine"], Ids(queue.Items));
        await keeper.StopAsync(Ct);
    }

    private async Task SaveOneTrackSessionAsync()
    {
        var (queue, player, keeper) = Create();
        await keeper.StartAsync(Ct);
        queue.Load(Tracks("a"));
        player.CurrentTrack = queue.Current!.Track;
        player.Position = TimeSpan.FromSeconds(10);
        await keeper.StopAsync(Ct);
        keeper.Dispose();
        Assert.True(File.Exists(SessionFile));
    }

    private TimeSpan? SavedPosition()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(SessionFile));
            return doc.RootElement.TryGetProperty("Position", out var p) ? TimeSpan.Parse(p.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private (QueueService Queue, FakePlayer Player, PlaybackSessionKeeper Keeper) Create()
    {
        var queue = new QueueService();
        var player = new FakePlayer();
        var keeper = new PlaybackSessionKeeper(player, queue, _settings, _paths, _time, NullLogger<PlaybackSessionKeeper>.Instance);
        return (queue, player, keeper);
    }
}
