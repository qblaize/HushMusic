using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using HushMusic.Core.Services;
using Xunit;
using static HushMusic.Core.Tests.TestData;

namespace HushMusic.Core.Tests;

/// <summary>Smart skip: the rules, the play tracker, the counts and the service that keeps them.</summary>
public sealed class SmartSkipTests
{
    private static readonly TimeSpan ThreeMinutes = TimeSpan.FromMinutes(3);

    private static Track Song(string videoId, TimeSpan? duration = null) => Track(videoId) with { Duration = duration ?? ThreeMinutes };

    private static TimeSpan Seconds(double value) => TimeSpan.FromSeconds(value);

    // ===== Rules =====

    [Theory]
    [InlineData(180, 30)] // 30 s for a normal song
    [InlineData(100, 25)] // a quarter of a short one
    [InlineData(0, 30)] // unknown length
    public void A_quick_skip_is_the_first_30_seconds_or_a_quarter_of_a_short_song(int durationSeconds, int limitSeconds) =>
        Assert.Equal(Seconds(limitSeconds), SkipRules.QuickSkipLimit(durationSeconds > 0 ? Seconds(durationSeconds) : null));

    [Fact]
    public void Half_the_song_is_a_listen_and_an_unknown_length_never_is_by_time()
    {
        Assert.True(SkipRules.IsListen(Seconds(90), ThreeMinutes));
        Assert.False(SkipRules.IsListen(Seconds(89), ThreeMinutes));
        Assert.False(SkipRules.IsListen(TimeSpan.FromHours(1), null));
    }

    // ===== Tracker =====

    [Fact]
    public void Next_within_the_first_seconds_is_a_quick_skip()
    {
        var tracker = new SkipTracker();
        var song = Song("a");
        tracker.Started(Guid.NewGuid(), song, TimeSpan.Zero, null, Seconds(0));

        var outcome = tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(12));

        Assert.Equal(SkipVerdict.QuickSkip, outcome.Verdict);
        Assert.Same(song, outcome.Track);
    }

    [Fact]
    public void Moving_on_after_the_window_is_not_a_quick_skip()
    {
        var tracker = new SkipTracker();
        tracker.Started(Guid.NewGuid(), Song("a"), TimeSpan.Zero, null, Seconds(0));

        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(31)).Verdict);
    }

    [Fact]
    public void Paused_time_does_not_count_as_heard()
    {
        var tracker = new SkipTracker();
        tracker.Started(Guid.NewGuid(), Song("a"), TimeSpan.Zero, null, Seconds(0));
        tracker.PlayingChanged(false, Seconds(10));

        // Ten minutes paused, then skipped right after resuming: still only 15 seconds heard.
        tracker.PlayingChanged(true, Seconds(610));

        Assert.Equal(SkipVerdict.QuickSkip, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(615)).Verdict);
    }

    [Fact]
    public void A_play_that_resumed_part_way_counts_from_where_it_started()
    {
        var tracker = new SkipTracker();
        tracker.Started(Guid.NewGuid(), Song("a"), Seconds(80), null, Seconds(0));

        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(5)).Verdict);
    }

    [Fact]
    public void Going_back_stopping_or_the_same_song_again_are_not_skips()
    {
        var tracker = new SkipTracker();
        var item = Guid.NewGuid();

        tracker.Started(item, Song("a"), TimeSpan.Zero, null, Seconds(0));
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: true, Seconds(5)).Verdict);

        tracker.Started(item, Song("a"), TimeSpan.Zero, null, Seconds(10));
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(null, null, wentBack: false, Seconds(15)).Verdict);

        // "Start radio" on the playing song puts it in a new queue item.
        tracker.Started(item, Song("a"), TimeSpan.Zero, null, Seconds(20));
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("a"), wentBack: false, Seconds(25)).Verdict);
    }

    [Fact]
    public void Live_radio_never_counts_on_either_side()
    {
        var tracker = new SkipTracker();
        var station = Track("radio:1") with { Station = new RadioStation { Id = "1", Name = "Station", StreamUrl = "https://example.com/stream" } };

        tracker.Started(Guid.NewGuid(), station, TimeSpan.Zero, null, Seconds(0));
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("a"), wentBack: false, Seconds(5)).Verdict);

        tracker.Started(Guid.NewGuid(), Song("a"), TimeSpan.Zero, null, Seconds(10));
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), station, wentBack: false, Seconds(15)).Verdict);
    }

    [Fact]
    public void A_song_skipped_while_loading_or_that_failed_does_not_count()
    {
        var tracker = new SkipTracker();

        // Never started: nothing to judge.
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(1)).Verdict);

        tracker.Started(Guid.NewGuid(), Song("a"), TimeSpan.Zero, null, Seconds(0));
        tracker.Failed(Song("a"));
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(3)).Verdict);
    }

    [Fact]
    public void Playing_to_the_end_or_past_half_is_a_listen()
    {
        var tracker = new SkipTracker();
        var item = Guid.NewGuid();
        tracker.Started(item, Song("a", TimeSpan.Zero), TimeSpan.Zero, null, Seconds(0));
        Assert.Equal(SkipVerdict.Listened, tracker.Completed(item, Song("a")).Verdict);

        tracker.Started(Guid.NewGuid(), Song("b"), TimeSpan.Zero, null, Seconds(0));
        Assert.Equal(SkipVerdict.Listened, tracker.MovedOn(Guid.NewGuid(), Song("c"), wentBack: false, Seconds(95)).Verdict);
    }

    [Fact]
    public void Half_a_song_counts_as_a_listen_as_soon_as_it_pauses_and_only_once()
    {
        var tracker = new SkipTracker();
        tracker.Started(Guid.NewGuid(), Song("a"), TimeSpan.Zero, null, Seconds(0));

        Assert.Equal(SkipVerdict.Listened, tracker.PlayingChanged(false, Seconds(100)).Verdict);
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(200)).Verdict);
    }

    [Fact]
    public void The_player_supplies_the_length_when_the_track_has_none()
    {
        var tracker = new SkipTracker();
        tracker.Started(Guid.NewGuid(), Track("a"), TimeSpan.Zero, null, Seconds(0));
        tracker.DurationKnown("a", Seconds(40));

        // A quarter of 40 s: moving on at 12 s isn't quick any more.
        Assert.Equal(SkipVerdict.None, tracker.MovedOn(Guid.NewGuid(), Song("b"), wentBack: false, Seconds(12)).Verdict);
    }

    // ===== Counts =====

    [Fact]
    public void Three_skips_make_a_song_often_skipped_and_a_listen_clears_it()
    {
        var counts = new SkipCounts();
        var at = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);

        Assert.False(counts.AddSkip(Song("a"), at));
        Assert.False(counts.AddSkip(Song("a"), at.AddMinutes(1)));
        Assert.True(counts.AddSkip(Song("a"), at.AddMinutes(2)));
        var song = Assert.Single(counts.OftenSkipped());
        Assert.Equal(("a", 3), (song.VideoId, song.Skips));

        Assert.True(counts.Clear("a"));
        Assert.Empty(counts.OftenSkipped());
        Assert.False(counts.Contains("a"));
    }

    [Fact]
    public void Saved_counts_are_added_to_skips_counted_before_the_file_was_read()
    {
        var counts = new SkipCounts();
        var at = new DateTimeOffset(2026, 10, 12, 10, 0, 0, TimeSpan.Zero);
        counts.AddSkip(Song("a"), at);

        counts.Merge([new SkippedSong("a", "Song a", string.Empty, null, 2, at.AddDays(-1)), new SkippedSong("b", "Song b", string.Empty, null, 1, at)]);

        Assert.Equal(["a"], counts.OftenSkipped().Select(s => s.VideoId));
        Assert.Equal(2, counts.Count);
    }

    [Fact]
    public void The_oldest_songs_that_are_not_often_skipped_make_room()
    {
        var counts = new SkipCounts();
        var at = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        counts.Merge([new SkippedSong("often", "Often", string.Empty, null, 5, at)]);
        for (var i = 0; i < SkipCounts.MaxSongs + 5; i++)
        {
            counts.AddSkip(Song("s" + i), at.AddMinutes(i));
        }

        Assert.Equal(SkipCounts.MaxSongs, counts.Count);
        Assert.True(counts.Contains("often"));
        Assert.False(counts.Contains("s0"));
        Assert.True(counts.Contains("s" + (SkipCounts.MaxSongs + 4)));
    }

    // ===== Filtering =====

    [Fact]
    public void Without_smart_skip_nothing_is_left_out()
    {
        ISmartSkip? none = null;
        Assert.Equal(["a", "b"], none.WithoutLeftOut(Tracks("a", "b")).Select(t => t.VideoId));

        var skip = new StubSmartSkip("b");
        Assert.Equal(["a", "c"], skip.WithoutLeftOut(Tracks("a", "b", "c")).Select(t => t.VideoId));
    }

    [Fact]
    public async Task Radio_up_next_leaves_out_often_skipped_songs_but_keeps_the_picked_one()
    {
        var queue = new QueueService();
        var watch = new FakeWatchApi { OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = Tracks(call.VideoId!, "x", "b", "y") }) };
        var actions = CreateActions(queue, watch, new StubSmartSkip("a", "b"));

        await actions.StartRadioAsync(Track("a"), TestContext.Current.CancellationToken);

        Assert.Equal(["a", "x", "y"], Ids(queue.Items));
    }

    [Fact]
    public async Task Playlists_the_user_plays_are_never_filtered()
    {
        var queue = new QueueService();
        var watch = new FakeWatchApi { OnGet = _ => Task.FromResult(new WatchPlaylist { Tracks = Tracks("a", "b", "c") }) };
        var actions = CreateActions(queue, watch, new StubSmartSkip("b"));

        await actions.PlayPlaylistAsync("PL1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["a", "b", "c"], Ids(queue.Items));
    }

    [Fact]
    public async Task Radio_top_ups_are_filtered_and_playlist_pages_are_not()
    {
        var queue = new QueueService();
        var watch = new FakeWatchApi { OnContinuation = (_, _) => Task.FromResult(new Paged<Track>(Tracks("p", "skipped", "q"), null)) };
        var extender = new QueueAutoExtender(queue, watch, NullLogger<QueueAutoExtender>.Instance, new StubSmartSkip("skipped"));
        await extender.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            queue.Load(Tracks("r"), 0, new QueueSource(QueueSourceKind.Radio, "r"), continuation: "next");
            await WaitUntilAsync(() => queue.Items.Count > 1);
            Assert.Equal(["r", "p", "q"], Ids(queue.Items));

            queue.Load(Tracks("l"), 0, new QueueSource(QueueSourceKind.Playlist, "PL1"), continuation: "next");
            await WaitUntilAsync(() => queue.Items.Count > 1);
            Assert.Equal(["l", "p", "skipped", "q"], Ids(queue.Items));
        }
        finally
        {
            await extender.StopAsync(CancellationToken.None);
            extender.Dispose();
        }
    }

    [Fact]
    public async Task Autoplay_leaves_out_often_skipped_songs()
    {
        var queue = new QueueService();
        var player = new FakePlayer();
        var watch = new FakeWatchApi { OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = Tracks(call.VideoId!, "s1", "skipped", "s2") }) };
        var autoplay = new QueueAutoplay(queue, player, watch, new FakeSettings(), NullLogger<QueueAutoplay>.Instance, new StubSmartSkip("skipped"));
        await autoplay.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            queue.Load(Tracks("a"), 0, new QueueSource(QueueSourceKind.Album, "MPREb_1"));
            player.RaiseTrackStarted(queue.Items[0]);
            await WaitUntilAsync(() => queue.Items.Count > 1);

            Assert.Equal(["a", "s1", "s2"], Ids(queue.Items));
        }
        finally
        {
            await autoplay.StopAsync(CancellationToken.None);
            autoplay.Dispose();
        }
    }

    // ===== Service =====

    [Fact]
    public async Task The_service_counts_quick_skips_and_saves_them()
    {
        using var folder = new TempFolder();
        var time = new ManualTimeProvider();
        var player = new FakePlayer { Duration = ThreeMinutes };
        var queue = new QueueService();
        var service = new SmartSkipService(player, queue, new FakeSettings(), new AppPaths(folder.Path), time, NullLogger<SmartSkipService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.LoadAsync(TestContext.Current.CancellationToken);
        var changes = 0;
        service.Changed += (_, _) => Interlocked.Increment(ref changes);

        for (var i = 0; i < 3; i++)
        {
            queue.Load(Tracks("a", "b"), 0, new QueueSource(QueueSourceKind.Radio, "a"));
            player.RaiseTrackStarted(queue.Items[0]);
            time.Advance(Seconds(5));
            player.RaiseTrackChanged(queue.Items[1].Track);
        }

        Assert.True(service.IsLeftOut("a"));
        Assert.False(service.IsLeftOut("b"));
        Assert.Equal(1, changes);
        await service.StopAsync(CancellationToken.None);

        // A new copy reads them back.
        var reloaded = new SmartSkipService(player, queue, new FakeSettings(), new AppPaths(folder.Path), time, NullLogger<SmartSkipService>.Instance);
        await reloaded.LoadAsync(TestContext.Current.CancellationToken);
        var song = Assert.Single(reloaded.OftenSkipped);
        Assert.Equal(("a", "Song a", 3), (song.VideoId, song.Title, song.Skips));

        await reloaded.ForgiveAsync("a", TestContext.Current.CancellationToken);
        Assert.Empty(reloaded.OftenSkipped);
        Assert.False(reloaded.IsLeftOut("a"));
    }

    [Fact]
    public async Task Turned_off_it_neither_counts_nor_filters()
    {
        using var folder = new TempFolder();
        var time = new ManualTimeProvider();
        var player = new FakePlayer { Duration = ThreeMinutes };
        var queue = new QueueService();
        var settings = new FakeSettings();
        var paths = new AppPaths(folder.Path);
        File.WriteAllText(
            Path.Combine(paths.Root, SmartSkipService.FileName),
            """{"version":1,"songs":[{"videoId":"old","title":"Old","artists":"","skips":4,"lastSkipped":"2026-10-01T10:00:00+00:00"}]}""");
        var service = new SmartSkipService(player, queue, settings, paths, time, NullLogger<SmartSkipService>.Instance);
        await service.StartAsync(TestContext.Current.CancellationToken);
        await service.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(service.IsLeftOut("old"));

        await settings.UpdateAsync(s => s.SmartSkip = false, TestContext.Current.CancellationToken);
        for (var i = 0; i < 3; i++)
        {
            queue.Load(Tracks("a", "b"), 0, new QueueSource(QueueSourceKind.Radio, "a"));
            player.RaiseTrackStarted(queue.Items[0]);
            time.Advance(Seconds(5));
            player.RaiseTrackChanged(queue.Items[1].Track);
        }

        Assert.False(service.IsLeftOut("old"));
        Assert.Equal(["old"], service.OftenSkipped.Select(s => s.VideoId));
        await service.StopAsync(CancellationToken.None);
    }

    private static PlaybackActions CreateActions(QueueService queue, FakeWatchApi watch, ISmartSkip smartSkip) =>
        new(queue, new FakePlayer(), watch, new FakeBrowseApi(), new FakeNotifications(), NullLogger<PlaybackActions>.Instance, smartSkip: smartSkip);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        Assert.True(condition());
    }

    private sealed class StubSmartSkip(params string[] leftOut) : ISmartSkip
    {
        public IReadOnlyList<SkippedSong> OftenSkipped => [];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public bool IsLeftOut(string videoId) => leftOut.Contains(videoId);

        public Task ForgiveAsync(string videoId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    /// <summary>A folder under %TEMP% that is deleted afterwards.</summary>
    private sealed class TempFolder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
