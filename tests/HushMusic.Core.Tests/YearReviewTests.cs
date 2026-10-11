using HushMusic.Core.Features.Stats;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>"Your year in Hush" (YearReviewCalculator) and the day streaks behind it (ListeningDays).</summary>
public sealed class YearReviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Only_the_calendar_year_counts_and_it_is_covered_up_to_today()
    {
        PlayRecord[] records =
        [
            Song("old", "a", new DateTimeOffset(2025, 12, 31, 23, 59, 0, TimeSpan.Zero), 120, 200),
            Song("1", "a", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), 120, 200),
            Song("2", "b", Now.AddDays(-1), 60, 200),
            Song("later", "c", Now.AddHours(1), 60, 200),
        ];

        var review = Compute(records, 2026);

        Assert.Equal(2, review.Plays);
        Assert.True(review.IsInProgress);
        Assert.Equal(new DateOnly(2026, 1, 1), review.FirstDay);
        Assert.Equal(new DateOnly(2026, 10, 8), review.LastDay);
        Assert.Equal(281, review.DaysCovered);
        Assert.Equal(365, review.ByDay.Count);
        Assert.Equal(TimeSpan.FromSeconds(180), review.MusicListened);
    }

    [Fact]
    public void A_past_year_is_complete_and_leap_years_have_366_days()
    {
        var review = Compute([Song("1", "a", new DateTimeOffset(2024, 2, 29, 12, 0, 0, TimeSpan.Zero), 120, 200)], 2024);

        Assert.False(review.IsInProgress);
        Assert.Equal(new DateOnly(2024, 12, 31), review.LastDay);
        Assert.Equal(366, review.ByDay.Count);
        Assert.Equal(TimeSpan.FromSeconds(120), review.ByDay[59]);
        Assert.Equal(new DateOnly(2024, 2, 29), review.BiggestDay);
    }

    [Fact]
    public void The_year_follows_the_local_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus3", TimeSpan.FromHours(3), "Plus3", "Plus3");

        // 22:00 UTC on New Year's Eve is already 1 a.m. on January 1st at +3.
        var record = Song("1", "a", new DateTimeOffset(2025, 12, 31, 22, 0, 0, TimeSpan.Zero), 120, 200);
        var review = YearReviewCalculator.Compute([record], 2026, Now, zone);

        Assert.Equal(1, review.Plays);
        Assert.Equal(TimeSpan.FromSeconds(120), review.ByHour[1]);
        Assert.Equal(0, YearReviewCalculator.Compute([record], 2025, Now, zone).Plays);
    }

    [Fact]
    public void Tops_are_ranked_like_the_stats_page_and_capped_at_five()
    {
        var records = Enumerable.Range(0, 8)
            .SelectMany(song => Enumerable.Range(0, song + 1).Select(play => Song($"{song}-{play}", "v" + song, Now.AddDays(-play - 1).AddMinutes(song), 60, 200)))
            .ToList();

        var review = Compute(records, 2026);

        Assert.Equal(["v7", "v6", "v5", "v4", "v3"], review.TopSongs.Select(s => s.VideoId));
        Assert.Equal([8, 7, 6, 5, 4], review.TopSongs.Select(s => s.Plays));
        Assert.Equal(5, review.TopArtists.Count);
        Assert.Equal(8, review.DistinctSongs);
        Assert.Equal(36, review.Plays);
    }

    [Fact]
    public void Peaks_name_the_busiest_hour_weekday_and_month()
    {
        PlayRecord[] records =
        [
            // Friday 2026-03-06 at 21:00 for an hour; Tuesday 2026-05-05 at 8:00 for ten minutes.
            Song("1", "a", new DateTimeOffset(2026, 3, 6, 21, 0, 0, TimeSpan.Zero), 3600, null),
            Song("2", "b", new DateTimeOffset(2026, 5, 5, 8, 0, 0, TimeSpan.Zero), 600, null),
        ];

        var review = Compute(records, 2026);

        Assert.Equal(21, review.PeakHour);
        Assert.Equal(DayOfWeek.Friday, review.PeakWeekday);
        Assert.Equal(3, review.PeakMonth);
        Assert.Equal(TimeSpan.FromHours(1), review.ByMonth[2]);
        Assert.Equal(new DateOnly(2026, 3, 6), review.BiggestDay);
        Assert.Equal(TimeSpan.FromHours(1), review.BiggestDayListened);
    }

    [Fact]
    public void Days_and_the_longest_streak_count_days_with_a_play()
    {
        var day = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero);
        PlayRecord[] records =
        [
            Song("1", "a", day, 60, 200),
            Song("2", "a", day.AddDays(1), 60, 200),
            Song("3", "a", day.AddDays(2), 60, 200),
            Song("skip", "a", day.AddDays(3), 12, 200),
            Song("4", "a", day.AddDays(4), 60, 200),
            Station("5", "st", day.AddDays(5), 1800),
            Song("6", "a", day.AddDays(10), 60, 200),
        ];

        var review = Compute(records, 2026);

        Assert.Equal(6, review.DaysListened);
        Assert.Equal(new Streak(new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 3)), review.LongestStreak);
        Assert.Equal(3, review.LongestStreak!.Value.Days);
    }

    [Fact]
    public void Radio_is_reported_on_its_own()
    {
        PlayRecord[] records =
        [
            Song("1", "a", Now.AddDays(-3), 120, 200),
            Station("2", "st-1", Now.AddDays(-2), 7200),
            Station("3", "st-2", Now.AddDays(-1), 600),
            Station("4", "st-3", Now.AddHours(-1), 15),
        ];

        var review = Compute(records, 2026);

        Assert.Equal(TimeSpan.FromSeconds(7815), review.RadioListened);
        Assert.Equal(2, review.Stations);
        Assert.Equal("st-1", review.TopStations[0].StationId);
        Assert.Equal(1, review.Plays);
    }

    [Fact]
    public void A_handful_of_plays_is_not_enough()
    {
        var records = Enumerable.Range(0, YearReview.MinimumPlays).Select(i => Song("p" + i, "v" + i, Now.AddDays(-i), 60, 200)).ToList();

        Assert.True(Compute(records, 2026).HasEnoughData);
        Assert.False(Compute(records.Skip(1), 2026).HasEnoughData);
        Assert.True(Compute([], 2026).ByDay.All(d => d == TimeSpan.Zero));
        Assert.Null(Compute([], 2026).PeakHour);
        Assert.Null(Compute([], 2026).BiggestDay);
    }

    [Fact]
    public void Years_list_every_year_with_listening_newest_first()
    {
        PlayRecord[] records =
        [
            Song("1", "a", new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero), 60, 200),
            Song("2", "a", new DateTimeOffset(2026, 6, 1, 12, 0, 0, TimeSpan.Zero), 60, 200),
            Song("3", "a", new DateTimeOffset(2026, 6, 2, 12, 0, 0, TimeSpan.Zero), 20, 200),
            Station("4", "st", new DateTimeOffset(2026, 6, 3, 12, 0, 0, TimeSpan.Zero), 600),
        ];

        var years = YearReviewCalculator.Years(records, TimeZoneInfo.Utc);

        Assert.Equal([2026, 2024], years.Select(y => y.Year));
        Assert.Equal(1, years[0].Plays);
        Assert.Equal(TimeSpan.FromSeconds(680), years[0].Listened);
        Assert.False(years[0].HasReview);
    }

    [Fact]
    public void Year_start_is_local_midnight()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Minus5", TimeSpan.FromHours(-5), "Minus5", "Minus5");

        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.FromHours(-5)), YearReviewCalculator.YearStart(2026, zone));
    }

    [Fact]
    public void Streaks_find_the_longest_run_and_the_current_one()
    {
        var d = new DateOnly(2026, 5, 10);
        HashSet<DateOnly> days = [d, d.AddDays(1), d.AddDays(3), d.AddDays(4), d.AddDays(5), d.AddDays(7), d.AddDays(8)];

        Assert.Equal(new Streak(d.AddDays(3), d.AddDays(5)), ListeningDays.Longest(days));
        Assert.Null(ListeningDays.Longest([]));

        // Today without a play yet keeps yesterday's streak alive; a day without one ends it.
        Assert.Equal(new Streak(d.AddDays(7), d.AddDays(8)), ListeningDays.Current(days, d.AddDays(8)));
        Assert.Equal(new Streak(d.AddDays(7), d.AddDays(8)), ListeningDays.Current(days, d.AddDays(9)));
        Assert.Null(ListeningDays.Current(days, d.AddDays(10)));
    }

    [Fact]
    public async Task The_service_reads_the_year_and_the_highlights()
    {
        var folder = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"), "history");
        try
        {
            var log = new JsonLinesPlayLog(folder, Microsoft.Extensions.Logging.Abstractions.NullLogger<JsonLinesPlayLog>.Instance);
            var ct = TestContext.Current.CancellationToken;
            await log.AppendAsync(Song("1", "a", new DateTimeOffset(2025, 3, 1, 12, 0, 0, TimeSpan.Zero), 60, 200), ct);
            for (var i = 0; i < 12; i++)
            {
                await log.AppendAsync(Song("n" + i, "v" + i, Now.AddDays(-i), 3600, 3600), ct);
            }

            IListeningStats stats = new ListeningStatsService(log, new FixedTime(Now));
            var review = await stats.GetYearReviewAsync(2026, ct);
            var highlights = await stats.GetHighlightsAsync(ct);

            Assert.Equal(12, review.Plays);
            Assert.Equal(12, review.LongestStreak!.Value.Days);
            Assert.Equal([2026, 2025], highlights.Years.Select(y => y.Year));
            Assert.True(highlights.Years[0].HasReview);
            Assert.Contains(highlights.Achievements.Badges, b => b.Goal.Id == "hours-10" && b.IsUnlocked);
            Assert.Equal(12, highlights.Achievements.CurrentStreak!.Value.Days);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(folder)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static YearReview Compute(IEnumerable<PlayRecord> records, int year) =>
        YearReviewCalculator.Compute(records, year, Now, TimeZoneInfo.Utc);

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
