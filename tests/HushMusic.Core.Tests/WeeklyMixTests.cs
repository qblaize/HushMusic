using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>"Fresh for you": when it is due, its seeds, how candidates are merged, and the service that builds it.</summary>
public sealed class WeeklyMixTests : IDisposable
{
    // A Wednesday.
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 18, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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

    // ===== Schedule =====

    [Theory]
    [InlineData("2026-10-14T18:00:00Z", "2026-10-12T00:00:00Z")] // Wednesday
    [InlineData("2026-10-12T00:00:00Z", "2026-10-12T00:00:00Z")] // Monday, midnight
    [InlineData("2026-10-18T23:59:00Z", "2026-10-12T00:00:00Z")] // Sunday night
    public void The_week_starts_on_Monday(string now, string monday) =>
        Assert.Equal(DateTimeOffset.Parse(monday, null), WeeklyMixPlanner.StartOfWeek(DateTimeOffset.Parse(now, null), Utc));

    [Fact]
    public void A_new_mix_is_due_on_the_first_run_on_or_after_Monday()
    {
        Assert.True(WeeklyMixPlanner.IsDue(null, Now, Utc));
        Assert.True(WeeklyMixPlanner.IsDue(new DateTimeOffset(2026, 10, 11, 23, 0, 0, TimeSpan.Zero), Now, Utc)); // last Sunday
        Assert.False(WeeklyMixPlanner.IsDue(new DateTimeOffset(2026, 10, 12, 9, 0, 0, TimeSpan.Zero), Now, Utc)); // this Monday
        Assert.False(WeeklyMixPlanner.IsDue(Now, Now, Utc));
        Assert.True(WeeklyMixPlanner.IsDue(Now.AddDays(3), Now, Utc)); // the clock was turned back
    }

    // ===== Seeds =====

    [Fact]
    public void Too_little_history_gives_no_seeds()
    {
        var plays = Enumerable.Range(0, WeeklyMixPlanner.MinimumPlays - 1).Select(i => Play("s" + i, "A" + i, Now.AddDays(-1))).ToList();

        // Plays that don't count (too short) and plays from before the window don't help either.
        plays.Add(Play("short", "A", Now.AddDays(-1)) with { ListenedSeconds = 5 });
        plays.Add(Play("old", "A", Now.AddDays(-60)));

        Assert.Null(WeeklyMixPlanner.PickSeeds(plays, Now));
    }

    [Fact]
    public void Seeds_are_the_most_played_songs_by_different_artists_and_the_most_played_artists()
    {
        List<PlayRecord> plays = [];
        plays.AddRange(Many(5, "top", "Alpha"));
        plays.AddRange(Many(4, "alpha2", "Alpha"));
        plays.AddRange(Many(3, "beta1", "Beta"));
        plays.AddRange(Many(2, "gamma1", "Gamma"));
        for (var i = 0; i < 10; i++)
        {
            plays.Add(Play("once" + i, "Solo" + i, Now.AddDays(-2)));
        }

        var seeds = WeeklyMixPlanner.PickSeeds(plays, Now);

        Assert.NotNull(seeds);
        Assert.Equal(24, seeds.Plays);
        Assert.Equal(WeeklyMixPlanner.SeedSongCount, seeds.Songs.Count);
        Assert.Equal(["top", "beta1", "gamma1"], seeds.Songs.Take(3));
        Assert.DoesNotContain("alpha2", seeds.Songs.Take(5)); // Alpha already has a seed
        Assert.Equal(["UCAlpha", "UCBeta", "UCGamma"], seeds.Artists.Take(3).Select(a => a.BrowseId));
        Assert.Equal(WeeklyMixPlanner.SeedArtistCount, seeds.Artists.Count);
    }

    // ===== Merge =====

    [Fact]
    public void Merge_takes_from_each_source_in_turn_and_leaves_out_what_does_not_belong()
    {
        IReadOnlyList<Track> radio =
        [
            Song("heard", "R1"),
            Song("r1", "R1"),
            Song("skipped", "R2"),
            Song("r2", "R2"),
            Song("dup", "R3"),
            Song("gone", "R4") with { IsAvailable = false },
            Song("episode", "R5") with { Type = TrackType.Episode },
        ];
        IReadOnlyList<Track> artist = [Song("a1", "Art"), Song("dup", "R3"), Song("a1-video", "Art") with { Title = "Song a1" }];

        var mix = WeeklyMixPlanner.Merge([radio, artist], HeardSongs.From([Play("heard", "R1", Now)]), id => id == "skipped", new Random(1));

        Assert.Equal(["a1", "dup", "r1", "r2"], mix.Select(t => t.VideoId).Order());
    }

    [Fact]
    public void A_song_heard_under_another_id_is_not_new()
    {
        // The music video was played; the album track of the same song comes up.
        var heard = HeardSongs.From([Play("video", "Queen", Now) with { Title = "Bohemian Rhapsody" }]);
        IReadOnlyList<Track> top = [Song("album-track", "QUEEN ") with { Title = "bohemian rhapsody" }, Song("other", "Queen")];

        var mix = WeeklyMixPlanner.Merge([top], heard, _ => false, new Random(1));

        Assert.Equal(["other"], mix.Select(t => t.VideoId));
    }

    [Theory]
    [InlineData("Never Gonna Give You Up (2022 Remaster)", "Rick Astley")]
    [InlineData("never gonna give you up [Official Video]", "RICK ASTLEY ")]
    [InlineData("Rick Astley - Never Gonna Give You Up", "Rick Astley")]
    public void Versions_of_a_song_count_as_the_same_song(string title, string artist) =>
        Assert.Equal(WeeklyMixPlanner.SongKey("Never Gonna Give You Up", "Rick Astley"), WeeklyMixPlanner.SongKey(title, artist));

    [Fact]
    public void Merge_caps_each_artist_and_the_size()
    {
        IReadOnlyList<Track> many = [.. Enumerable.Range(0, 10).Select(i => Song("same" + i, "Same"))];
        IReadOnlyList<Track> others = [.. Enumerable.Range(0, 80).Select(i => Song("o" + i, "Other" + i))];

        var capped = WeeklyMixPlanner.Merge([many], HeardSongs.From([]), _ => false, new Random(1));
        var full = WeeklyMixPlanner.Merge([many, others], HeardSongs.From([]), _ => false, new Random(1));

        Assert.Equal(WeeklyMixPlanner.MaxPerArtist, capped.Count);
        Assert.Equal(WeeklyMixPlanner.TargetSize, full.Count);
        Assert.Equal(full.Count, full.Select(t => t.VideoId).Distinct().Count());
    }

    // ===== Service =====

    [Fact]
    public async Task Builds_a_mix_of_new_songs_with_few_requests_and_keeps_it()
    {
        var log = new MemoryLog(Enumerable.Range(0, 30).Select(i => Play("p" + (i % 10), "Artist" + (i % 10), Now.AddDays(-i))));
        var watch = new FakeWatchApi
        {
            OnGet = call => Task.FromResult(new WatchPlaylist
            {
                Tracks = [Song(call.VideoId!, "Seed"), Song("p1", "Artist1"), .. Enumerable.Range(0, 4).Select(i => Song($"{call.VideoId}-r{i}", $"{call.VideoId}-artist{i}"))],
                Continuation = "more-" + call.VideoId,
            }),
            OnContinuation = (token, _) => Task.FromResult(new Paged<Track>([.. Enumerable.Range(0, 4).Select(i => Song($"{token}-c{i}", $"{token}-artist{i}"))], token + "+")),
        };
        var browse = new ArtistBrowseApi();
        var service = CreateService(log, watch, browse);

        await service.CheckAsync();

        var mix = service.Current;
        Assert.NotNull(mix);
        Assert.Equal(Now, mix.BuiltAt);
        var ids = mix.Tracks.Select(t => t.VideoId).ToList();
        Assert.Empty(ids.Intersect(Enumerable.Range(0, 10).Select(i => "p" + i)));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.Contains(ids, id => id.StartsWith("top-", StringComparison.Ordinal));
        Assert.True(ids.Count <= WeeklyMixPlanner.TargetSize);
        Assert.Equal(WeeklyMixService.MaxRequests, watch.Calls.Count + watch.ContinuationCalls.Count + browse.Calls.Count);
        Assert.All(watch.Calls, c => Assert.True(c.Radio));

        // Not due again this week.
        await service.CheckAsync();
        Assert.Equal(WeeklyMixService.MaxRequests, watch.Calls.Count + watch.ContinuationCalls.Count + browse.Calls.Count);

        // Kept on disk for the next start.
        var reloaded = CreateService(log, watch, browse);
        await reloaded.LoadAsync(Ct);
        Assert.Equal(ids, reloaded.Current?.Tracks.Select(t => t.VideoId));
        Assert.Equal(mix.Tracks[0].Title, reloaded.Current?.Tracks[0].Title);
    }

    [Fact]
    public async Task Nothing_is_built_without_enough_history()
    {
        var log = new MemoryLog(Enumerable.Range(0, 5).Select(i => Play("p" + i, "Artist", Now.AddDays(-1))));
        var watch = new FakeWatchApi();
        var browse = new ArtistBrowseApi();
        var service = CreateService(log, watch, browse);

        await service.CheckAsync();

        Assert.Null(service.Current);
        Assert.Empty(watch.Calls);
        Assert.Empty(browse.Calls);
        Assert.False(File.Exists(Path.Combine(_root, WeeklyMixService.FileName)));
    }

    [Fact]
    public async Task Often_skipped_songs_stay_out_of_the_mix()
    {
        var log = new MemoryLog(Enumerable.Range(0, 30).Select(i => Play("p" + (i % 10), "Artist" + (i % 10), Now.AddDays(-i))));
        var watch = new FakeWatchApi
        {
            OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = [.. Enumerable.Range(0, 5).Select(i => Song($"{call.VideoId}-r{i}", $"{call.VideoId}-artist{i}"))] }),
        };
        var service = CreateService(log, watch, new ArtistBrowseApi(), new LeftOut("p0-r0", "p1-r1"));

        await service.CheckAsync();

        var ids = service.Current!.Tracks.Select(t => t.VideoId).ToList();
        Assert.Contains("p0-r1", ids);
        Assert.DoesNotContain("p0-r0", ids);
        Assert.DoesNotContain("p1-r1", ids);
    }

    [Fact]
    public async Task A_song_that_becomes_often_skipped_leaves_the_mix_until_it_is_forgiven()
    {
        var log = new MemoryLog(Enumerable.Range(0, 30).Select(i => Play("p" + (i % 10), "Artist" + (i % 10), Now.AddDays(-i))));
        var watch = new FakeWatchApi
        {
            OnGet = call => Task.FromResult(new WatchPlaylist { Tracks = [.. Enumerable.Range(0, 5).Select(i => Song($"{call.VideoId}-r{i}", $"{call.VideoId}-artist{i}"))] }),
        };
        var skips = new LeftOut();
        var service = CreateService(log, watch, new ArtistBrowseApi(), skips);
        await service.StartAsync(Ct);
        try
        {
            await service.CheckAsync();
            var count = service.Current!.Tracks.Count;
            var song = service.Current.Tracks[0].VideoId;
            var changes = 0;
            service.Changed += (_, _) => changes++;

            skips.Add(song);

            Assert.Equal(1, changes);
            Assert.Equal(count - 1, service.Current!.Tracks.Count);
            Assert.DoesNotContain(song, service.Current.Tracks.Select(t => t.VideoId));

            await skips.ForgiveAsync(song, Ct);

            Assert.Equal(count, service.Current!.Tracks.Count);
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }
    }

    private WeeklyMixService CreateService(IPlayLog log, IWatchApi watch, IBrowseApi browse, ISmartSkip? smartSkip = null) =>
        new(log, watch, browse, new FakePlayer(), new AppPaths(_root), new Clock(Now), NullLogger<WeeklyMixService>.Instance, smartSkip)
        {
            RequestGap = TimeSpan.Zero,
        };

    private static Track Song(string videoId, string artist) => TestData.Track(videoId) with
    {
        Artists = [new ArtistRef(artist, "UC" + artist)],
        Duration = TimeSpan.FromMinutes(3),
    };

    private static PlayRecord Play(string videoId, string artist, DateTimeOffset start) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        Kind = PlayKind.Track,
        ItemId = videoId,
        Title = "Song " + videoId,
        Artists = [new PlayArtist(artist, "UC" + artist)],
        Start = start,
        ListenedSeconds = 120,
        DurationSeconds = 180,
    };

    private static IEnumerable<PlayRecord> Many(int count, string videoId, string artist) =>
        Enumerable.Range(0, count).Select(i => Play(videoId, artist, Now.AddDays(-1 - i)));

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MemoryLog(IEnumerable<PlayRecord> records) : IPlayLog
    {
        private readonly List<PlayRecord> _records = [.. records];

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public Task AppendAsync(PlayRecord record, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PlayRecord>> ReadAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<PlayRecord>>([.. _records.Where(r => since is null || r.Start >= since).OrderBy(r => r.Start)]);

        public Task ClearAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    /// <summary>Every artist page has three top songs of its own.</summary>
    private sealed class ArtistBrowseApi : IBrowseApi
    {
        public List<string> Calls { get; } = [];

        public Task<ArtistPage> GetArtistAsync(string channelId, CancellationToken cancellationToken = default)
        {
            Calls.Add(channelId);
            var name = channelId["UC".Length..];
            return Task.FromResult(new ArtistPage
            {
                Artist = new Artist { Title = name, BrowseId = channelId },
                TopSongs = [.. Enumerable.Range(0, 3).Select(i => Song($"top-{name}-{i}", name))],
            });
        }

        public Task<Paged<Shelf>> GetHomeAsync(string? continuation = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Paged<Album>> GetArtistAlbumsAsync(string browseId, string? browseParams, string? continuation = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<Paged<Track>> GetPlaylistTracksAsync(string continuation, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LeftOut(params string[] videoIds) : ISmartSkip
    {
        private readonly HashSet<string> _videoIds = [.. videoIds];

        public IReadOnlyList<SkippedSong> OftenSkipped => [];

        public event EventHandler? Changed;

        public bool IsLeftOut(string videoId) => _videoIds.Contains(videoId);

        /// <summary>The song became often skipped.</summary>
        public void Add(string videoId)
        {
            _videoIds.Add(videoId);
            Changed?.Invoke(this, EventArgs.Empty);
        }

        public Task ForgiveAsync(string videoId, CancellationToken cancellationToken = default)
        {
            _videoIds.Remove(videoId);
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
