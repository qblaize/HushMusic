using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Features.Stats;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>The play log files (JsonLinesPlayLog) and the statistics computed from them (ListeningStatsCalculator).</summary>
public sealed class ListeningStatsTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 20, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"), "history");
    private readonly JsonLinesPlayLog _log;

    public ListeningStatsTests()
    {
        _log = new JsonLinesPlayLog(_folder, NullLogger<JsonLinesPlayLog>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // ===== The log =====

    [Fact]
    public async Task Lines_are_json_in_a_file_per_year()
    {
        await _log.AppendAsync(Song("p1", "a", Now, 61, 200) with { Artists = [new PlayArtist("Artist", "UC1")], Album = "Album", AlbumId = "MPREb_1" }, Ct);

        var line = File.ReadAllText(Path.Combine(_folder, "plays-2026.jsonl"));
        Assert.Equal(
            """{"v":1,"id":"p1","kind":"track","item":"a","title":"Song a","artists":[{"name":"Artist","id":"UC1"}],"album":"Album","albumId":"MPREb_1","start":"2026-10-08T20:00:00+00:00","listened":61,"duration":200}""" + "\n",
            line);
    }

    [Fact]
    public async Task Accented_titles_are_written_as_is_and_read_back()
    {
        await _log.AppendAsync(Song("p1", "a", Now, 61, 200) with { Title = "Azi pot să mor fericit" }, Ct);

        Assert.Contains("\"title\":\"Azi pot să mor fericit\"", await File.ReadAllTextAsync(Path.Combine(_folder, "plays-2026.jsonl"), Ct));
        Assert.Equal("Azi pot să mor fericit", Assert.Single(await _log.ReadAsync(null, Ct)).Title);
    }

    [Fact]
    public async Task Reading_keeps_the_longest_line_of_each_play()
    {
        await _log.AppendAsync(Song("p1", "a", Now, 30, 200), Ct);
        await _log.AppendAsync(Song("p2", "b", Now.AddMinutes(4), 45, 200), Ct);
        await _log.AppendAsync(Song("p1", "a", Now, 190, 200), Ct);

        var records = await _log.ReadAsync(null, Ct);

        Assert.Equal(["p1", "p2"], records.Select(r => r.Id));
        Assert.Equal(190, records[0].ListenedSeconds);
    }

    [Fact]
    public async Task A_line_cut_short_by_a_crash_is_skipped_and_the_next_one_still_reads()
    {
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, "plays-2026.jsonl");
        await File.WriteAllTextAsync(file, """{"v":1,"id":"ok","kind":"track","item":"a","title":"A","start":"2026-10-08T19:00:00+00:00","listened":40}""" + "\n" + """{"v":1,"id":"torn","kind":"tra""", Ct);

        await _log.AppendAsync(Song("p2", "b", Now, 50, null), Ct);
        var records = await _log.ReadAsync(null, Ct);

        Assert.Equal(["ok", "p2"], records.Select(r => r.Id));
    }

    [Fact]
    public async Task Garbage_lines_and_unknown_kinds_are_skipped()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(
            Path.Combine(_folder, "plays-2025.jsonl"),
            "not json\n{}\n" + """{"v":1,"id":"x","kind":"podcast","item":"a","title":"A","start":"2025-10-08T19:00:00+00:00","listened":40}""" + "\n\n",
            Ct);

        Assert.Empty(await _log.ReadAsync(null, Ct));
    }

    [Fact]
    public async Task Concurrent_writers_never_mix_lines()
    {
        // Two logs on the same folder stand in for two copies of the app.
        var other = new JsonLinesPlayLog(_folder, NullLogger<JsonLinesPlayLog>.Instance);
        var writes = Enumerable.Range(0, 200).Select(i => Task.Run(
            () => (i % 2 == 0 ? _log : other).AppendAsync(Song("p" + i, "v" + i, Now.AddSeconds(i), 40 + i, 200), Ct),
            Ct));
        await Task.WhenAll(writes);

        var lines = (await File.ReadAllLinesAsync(Path.Combine(_folder, "plays-2026.jsonl"), Ct)).Where(l => l.Length > 0).ToList();
        Assert.Equal(200, lines.Count);
        Assert.All(lines, l => Assert.NotNull(JsonLinesPlayLog.TryParse(l)));
        Assert.Equal(200, (await _log.ReadAsync(null, Ct)).Count);
    }

    [Fact]
    public async Task Reading_a_period_skips_older_plays_and_files()
    {
        await _log.AppendAsync(Song("old", "a", new DateTimeOffset(2024, 5, 1, 12, 0, 0, TimeSpan.Zero), 100, 200), Ct);
        await _log.AppendAsync(Song("new", "b", Now.AddDays(-2), 100, 200), Ct);

        var recent = await _log.ReadAsync(Now.AddDays(-7), Ct);
        var all = await _log.ReadAsync(null, Ct);

        Assert.Equal(["new"], recent.Select(r => r.Id));
        Assert.Equal(["old", "new"], all.Select(r => r.Id));
        Assert.True(File.Exists(Path.Combine(_folder, "plays-2024.jsonl")));
    }

    [Fact]
    public async Task Clearing_deletes_every_year_and_raises_changed()
    {
        var changed = 0;
        _log.Changed += (_, _) => changed++;
        await _log.AppendAsync(Song("a", "a", new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), 100, 200), Ct);
        await _log.AppendAsync(Song("b", "b", Now, 100, 200), Ct);

        await _log.ClearAsync(Ct);

        Assert.Empty(await _log.ReadAsync(null, Ct));
        Assert.Empty(Directory.EnumerateFiles(_folder));
        Assert.Equal(3, changed);
    }

    [Fact]
    public async Task Reading_without_a_log_is_empty()
    {
        Assert.Empty(await _log.ReadAsync(null, Ct));
        await _log.ClearAsync(Ct);
    }

    // ===== The statistics =====

    [Fact]
    public void Plays_count_after_30_seconds_or_half_the_song()
    {
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddHours(-1), 30, 200),
            Song("2", "a", Now.AddHours(-2), 29, 200),
            Song("3", "b", Now.AddHours(-3), 20, 40),
            Song("4", "c", Now.AddHours(-4), 25, null),
        ];

        var summary = Compute(records, StatsPeriod.Week);

        Assert.Equal(2, summary.Plays);
        Assert.Equal(2, summary.DistinctSongs);
        Assert.Equal(TimeSpan.FromSeconds(104), summary.MusicListened);
    }

    [Fact]
    public void Periods_end_now_and_reach_back_7_30_and_365_days()
    {
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddDays(-1), 60, 200),
            Song("2", "a", Now.AddDays(-8), 60, 200),
            Song("3", "a", Now.AddDays(-40), 60, 200),
            Song("4", "a", Now.AddDays(-400), 60, 200),
            Song("5", "a", Now.AddHours(1), 60, 200),
        ];

        Assert.Equal(1, Compute(records, StatsPeriod.Week).Plays);
        Assert.Equal(2, Compute(records, StatsPeriod.Month).Plays);
        Assert.Equal(3, Compute(records, StatsPeriod.Year).Plays);
        Assert.Equal(4, Compute(records, StatsPeriod.AllTime).Plays);
        Assert.Null(Compute(records, StatsPeriod.AllTime).From);
        Assert.Equal(Now.AddDays(-7), Compute(records, StatsPeriod.Week).From);
    }

    [Fact]
    public void Top_songs_rank_by_plays_then_time()
    {
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddHours(-1), 200, 200),
            Song("2", "b", Now.AddHours(-2), 60, 200),
            Song("3", "b", Now.AddHours(-3), 60, 200),
            Song("4", "c", Now.AddHours(-4), 100, 200),
            Song("5", "d", Now.AddHours(-5), 15, 200),
        ];

        var top = Compute(records, StatsPeriod.Week).TopSongs;

        Assert.Equal(["b", "a", "c"], top.Select(s => s.VideoId));
        Assert.Equal([2, 1, 1], top.Select(s => s.Plays));
        Assert.Equal(TimeSpan.FromMinutes(2), top[0].Listened);
    }

    [Fact]
    public void Every_credited_artist_gets_the_play()
    {
        var duet = new[] { new PlayArtist("Ann", "UC_A"), new PlayArtist("Bob", "UC_B") };
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddHours(-1), 60, 200) with { Artists = duet, ArtUrl = "art-a" },
            Song("2", "b", Now.AddHours(-2), 60, 200) with { Artists = [new PlayArtist("Bob", "UC_B")], ArtUrl = "art-b" },
            Song("3", "c", Now.AddHours(-3), 60, 200) with { Artists = [new PlayArtist("Bob", "UC_B")], ArtUrl = "art-c" },
            Song("4", "d", Now.AddHours(-4), 60, 200) with { Artists = [new PlayArtist("Cat", null)] },
            Song("5", "e", Now.AddHours(-5), 60, 200) with { Artists = [new PlayArtist("cat", null)] },
        ];

        var summary = Compute(records, StatsPeriod.Week);

        Assert.Equal(["Bob", "Cat", "Ann"], summary.TopArtists.Select(a => a.Name));
        Assert.Equal([3, 2, 1], summary.TopArtists.Select(a => a.Plays));
        Assert.Equal("UC_B", summary.TopArtists[0].BrowseId);
        Assert.Null(summary.TopArtists[1].BrowseId);
        Assert.Equal(3, summary.DistinctArtists);
        Assert.Equal("art-a", summary.TopArtists[2].ArtUrl);
    }

    [Fact]
    public void Albums_group_by_id_and_skip_songs_without_one()
    {
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddHours(-1), 60, 200) with { Album = "First", AlbumId = "MPREb_1", ArtUrl = "old" },
            Song("2", "b", Now.AddMinutes(-1), 60, 200) with { Album = "First", AlbumId = "MPREb_1", ArtUrl = "new" },
            Song("3", "c", Now.AddHours(-3), 60, 200) with { Album = "Second" },
            Song("4", "d", Now.AddHours(-4), 60, 200),
        ];

        var albums = Compute(records, StatsPeriod.Week).TopAlbums;

        Assert.Equal(["First", "Second"], albums.Select(a => a.Title));
        Assert.Equal([2, 1], albums.Select(a => a.Plays));
        Assert.Equal("MPREb_1", albums[0].BrowseId);
        Assert.Equal("new", albums[0].ArtUrl);
    }

    [Fact]
    public void Radio_time_is_counted_apart_from_music()
    {
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddHours(-1), 120, 200),
            Station("2", "st-1", Now.AddHours(-5), 3600),
            Station("3", "st-2", Now.AddHours(-3), 600),
            Station("4", "st-1", Now.AddHours(-2), 20),
        ];

        var summary = Compute(records, StatsPeriod.Week);

        Assert.Equal(TimeSpan.FromSeconds(120), summary.MusicListened);
        Assert.Equal(TimeSpan.FromSeconds(4220), summary.RadioListened);
        Assert.Equal(TimeSpan.FromSeconds(4340), summary.TotalListened);
        Assert.Equal(1, summary.Plays);
        Assert.Equal(["st-1", "st-2"], summary.TopStations.Select(s => s.StationId));
        Assert.Equal([1, 1], summary.TopStations.Select(s => s.Sessions));
        Assert.Equal(TimeSpan.FromSeconds(3620), summary.TopStations[0].Listened);
        Assert.Equal(["Artist a"], summary.TopArtists.Select(a => a.Name));
    }

    [Fact]
    public void Listening_is_spread_over_the_hours_and_days_it_covered()
    {
        // Sunday 2026-10-04 23:30 for 90 minutes: 30 minutes on Sunday at 23h, 60 minutes on Monday at 0h.
        var start = new DateTimeOffset(2026, 10, 4, 23, 30, 0, TimeSpan.Zero);
        var summary = Compute([Station("1", "st", start, 5400)], StatsPeriod.Week);

        Assert.Equal(TimeSpan.FromMinutes(30), summary.ByHour[23]);
        Assert.Equal(TimeSpan.FromMinutes(60), summary.ByHour[0]);
        Assert.Equal(TimeSpan.FromMinutes(60), summary.ByWeekday[0]);
        Assert.Equal(TimeSpan.FromMinutes(30), summary.ByWeekday[6]);
        Assert.Equal(TimeSpan.FromMinutes(90), summary.ByHour.Aggregate(TimeSpan.Zero, (a, b) => a + b));
    }

    [Fact]
    public void Hours_follow_the_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus3", TimeSpan.FromHours(3), "Plus3", "Plus3");
        var summary = ListeningStatsCalculator.Compute([Song("1", "a", new DateTimeOffset(2026, 10, 8, 10, 0, 0, TimeSpan.Zero), 60, 200)], StatsPeriod.Week, Now, zone);

        Assert.Equal(TimeSpan.FromMinutes(1), summary.ByHour[13]);
    }

    [Fact]
    public void No_plays_is_empty()
    {
        var summary = Compute([], StatsPeriod.AllTime);

        Assert.True(summary.IsEmpty);
        Assert.Equal(24, summary.ByHour.Count);
        Assert.Equal(7, summary.ByWeekday.Count);
    }

    [Fact]
    public void Top_lists_are_capped()
    {
        var records = Enumerable.Range(0, 30).Select(i => Song("p" + i, "v" + i, Now.AddMinutes(-i), 60, 200)).ToList();

        var summary = ListeningStatsCalculator.Compute(records, StatsPeriod.Week, Now, TimeZoneInfo.Utc, topCount: 5);

        Assert.Equal(5, summary.TopSongs.Count);
        Assert.Equal(30, summary.Plays);
    }

    [Fact]
    public async Task The_service_reads_the_period_and_computes()
    {
        await _log.AppendAsync(Song("1", "a", Now.AddDays(-1), 60, 200), Ct);
        await _log.AppendAsync(Song("2", "b", Now.AddDays(-20), 60, 200), Ct);
        IListeningStats stats = new ListeningStatsService(_log, new FixedTime(Now));

        var week = await stats.GetSummaryAsync(StatsPeriod.Week, Ct);
        var month = await stats.GetSummaryAsync(StatsPeriod.Month, Ct);
        await stats.ClearAsync(Ct);

        Assert.Equal(1, week.Plays);
        Assert.Equal(2, month.Plays);
        Assert.True((await stats.GetSummaryAsync(StatsPeriod.AllTime, Ct)).IsEmpty);
    }

    private static ListeningSummary Compute(IEnumerable<PlayRecord> records, StatsPeriod period) =>
        ListeningStatsCalculator.Compute(records, period, Now, TimeZoneInfo.Utc);

    private static PlayRecord Song(string id, string videoId, DateTimeOffset start, int listened, int? duration) => new()
    {
        Id = id,
        Kind = PlayKind.Track,
        ItemId = videoId,
        Title = "Song " + videoId,
        Artists = [new PlayArtist("Artist " + videoId, null)],
        Start = start,
        ListenedSeconds = listened,
        DurationSeconds = duration,
    };

    private static PlayRecord Station(string id, string stationId, DateTimeOffset start, int listened) => new()
    {
        Id = id,
        Kind = PlayKind.Radio,
        ItemId = stationId,
        Title = "Station " + stationId,
        Start = start,
        ListenedSeconds = listened,
    };

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
