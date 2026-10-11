using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Features.Stats;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>The badges of "Your stats" (Achievements, AchievementTally) and their unlock notices (AchievementWatcher).</summary>
public sealed class AchievementTests : IDisposable
{
    private static readonly DateTimeOffset Day = new(2026, 3, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "hushmusic-tests", Guid.NewGuid().ToString("N"), "history");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path.GetDirectoryName(_folder)!, recursive: true);
        }
        catch (IOException)
        {
            // Never created (the test wrote nothing), or still in use.
        }
    }

    [Fact]
    public void Goal_ids_are_unique_and_every_kind_has_one()
    {
        Assert.Equal(Achievements.Goals.Count, Achievements.Goals.Select(g => g.Id).Distinct().Count());
        Assert.All(Enum.GetValues<AchievementKind>(), kind => Assert.Contains(Achievements.Goals, g => g.Kind == kind));
    }

    [Fact]
    public void Nothing_played_unlocks_nothing()
    {
        var report = Report([]);

        Assert.Equal(0, report.Unlocked);
        Assert.Null(report.CurrentStreak);
        Assert.All(report.Badges, b => Assert.Equal(0, b.Progress));
    }

    [Fact]
    public void Listening_hours_unlock_on_the_day_they_are_reached_and_show_progress()
    {
        // Eleven 1-hour plays on consecutive days: the 10th hour ends on the 10th day.
        var records = Enumerable.Range(0, 11).Select(i => Song("p" + i, "v" + i, Day.AddDays(i), 3600, 3600)).ToList();

        var report = Report(records);

        Assert.Equal(new DateOnly(2026, 3, 11), Badge(report, "hours-10").UnlockedOn);
        Assert.False(Badge(report, "hours-100").IsUnlocked);
        Assert.Equal(11, Badge(report, "hours-100").Progress);
        Assert.Equal(0.11, Badge(report, "hours-100").Ratio, 3);
    }

    [Fact]
    public void Radio_counts_for_listening_hours_and_its_own_badges()
    {
        PlayRecord[] records = [Station("1", "st", Day, 6 * 3600), Station("2", "st", Day.AddDays(1), 5 * 3600)];

        var report = Report(records);

        Assert.True(Badge(report, "radio-10").IsUnlocked);
        Assert.True(Badge(report, "hours-10").IsUnlocked);
        Assert.True(Badge(report, "marathon").IsUnlocked);
        Assert.Equal(DateOnly.FromDateTime(Day.DateTime), Badge(report, "marathon").UnlockedOn);
        Assert.Equal(0, Badge(report, "songs-100").Progress);
    }

    [Fact]
    public void Streaks_unlock_on_the_seventh_day_and_report_current_and_longest()
    {
        // Days 0–7 in a row, a gap, then days 10–11.
        var days = Enumerable.Range(0, 8).Concat([10, 11]);
        var records = days.Select(i => Song("p" + i, "a", Day.AddDays(i), 60, 200)).ToList();

        var report = Achievements.Compute(records, new DateOnly(2026, 3, 13), TimeZoneInfo.Utc);

        Assert.Equal(new DateOnly(2026, 3, 8), Badge(report, "streak-7").UnlockedOn);
        Assert.Equal(8, report.LongestStreak!.Value.Days);
        Assert.Equal(new Streak(new DateOnly(2026, 3, 12), new DateOnly(2026, 3, 13)), report.CurrentStreak);
        Assert.Equal(8, Badge(report, "streak-30").Progress);
    }

    [Fact]
    public void Songs_and_artists_count_different_ones_played_long_enough()
    {
        var records = Enumerable.Range(0, 120)
            .Select(i => Song("p" + i, "v" + (i % 100), Day.AddMinutes(i * 4), i < 110 ? 60 : 5, 200) with
            {
                Artists = [new PlayArtist("Artist " + (i % 60), null), new PlayArtist("Featured", "UC_F")],
            })
            .ToList();

        var report = Report(records);

        Assert.True(Badge(report, "songs-100").IsUnlocked);
        Assert.Equal(100, Badge(report, "songs-100").Progress);
        Assert.True(Badge(report, "artists-50").IsUnlocked);
        Assert.Equal(61, Badge(report, "artists-250").Progress);
    }

    [Fact]
    public void Night_owls_and_early_birds_count_songs_by_the_hour_they_started()
    {
        var night = new DateTimeOffset(2026, 3, 2, 0, 0, 0, TimeSpan.Zero);
        var records = Enumerable.Range(0, 50).Select(i => Song("n" + i, "v" + i, night.AddDays(i).AddMinutes(i), 60, 200))
            .Concat(Enumerable.Range(0, 49).Select(i => Song("m" + i, "v" + i, night.AddDays(i).AddHours(6), 60, 200)))
            .Append(Song("late", "x", night.AddHours(4), 60, 200))
            .Append(Song("dawn", "x", night.AddHours(8), 60, 200))
            .ToList();

        var report = Report(records);

        Assert.True(Badge(report, "night-owl").IsUnlocked);
        Assert.Equal(new DateOnly(2026, 4, 20), Badge(report, "night-owl").UnlockedOn);
        Assert.False(Badge(report, "early-bird").IsUnlocked);
        Assert.Equal(49, Badge(report, "early-bird").Progress);
    }

    [Fact]
    public void Later_lines_of_a_play_only_add_what_was_heard_since()
    {
        var tally = new AchievementTally(TimeZoneInfo.Utc);
        tally.Add(Song("p", "a", Day, 40, 200));
        tally.Add(Song("p", "a", Day, 30, 200));
        tally.Add(Song("p", "a", Day, 9 * 3600, null));
        tally.Add(Song("p", "a", Day, 9 * 3600, null));

        var report = tally.Report(new DateOnly(2026, 3, 2));

        Assert.Equal(9, Badge(report, "hours-10").Progress);
        Assert.Equal(1, Badge(report, "songs-100").Progress);
        Assert.Equal(1, report.CurrentStreak!.Value.Days);
    }

    [Fact]
    public void Unlock_days_follow_the_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus3", TimeSpan.FromHours(3), "Plus3", "Plus3");

        // 22:30 UTC on March 1st is 1:30 a.m. on March 2nd at +3: a night owl's song, on the 2nd.
        var tally = new AchievementTally(zone);
        for (var i = 0; i < 50; i++)
        {
            tally.Add(Song("n" + i, "v" + i, new DateTimeOffset(2026, 3, 1, 22, 30, 0, TimeSpan.Zero), 60, 200));
        }

        Assert.Equal(new DateOnly(2026, 3, 2), Badge(tally.Report(new DateOnly(2026, 3, 2)), "night-owl").UnlockedOn);
    }

    [Fact]
    public async Task The_first_check_takes_note_quietly_then_new_badges_are_announced_once()
    {
        var log = new JsonLinesPlayLog(_folder, NullLogger<JsonLinesPlayLog>.Instance);
        var notifications = new FakeNotifications();
        var time = new ManualTimeProvider();
        var file = Path.Combine(_folder, AchievementWatcher.FileName);
        using var watcher = new AchievementWatcher(log, notifications, file, time, NullLogger<AchievementWatcher>.Instance);
        await watcher.StartAsync(Ct);

        // Already earned before: 11 hours, on 11 days in a row.
        for (var i = 0; i < 11; i++)
        {
            await log.AppendAsync(Song("p" + i, "v" + i, Day.AddDays(i), 3600, 3600), Ct);
        }

        await watcher.CheckAsync(Ct);
        Assert.Empty(notifications.Shown);
        Assert.Contains("hours-10", await File.ReadAllTextAsync(file, Ct));

        // 100 hours and a marathon day at once: one notice for both.
        await log.AppendAsync(Song("long", "x", Day.AddDays(11), 90 * 3600, null), Ct);
        await watcher.CheckAsync(Ct);
        var notice = Assert.Single(notifications.Shown);
        Assert.Equal("2 achievements unlocked", notice.Title);
        Assert.Equal("100 hours, Marathon", notice.Message);

        // Clearing and earning them again announces nothing new.
        await log.ClearAsync(Ct);
        await log.AppendAsync(Song("again", "x", Day, 11 * 3600, null), Ct);
        await watcher.CheckAsync(Ct);
        Assert.Single(notifications.Shown);

        await watcher.StopAsync(Ct);
    }

    [Fact]
    public async Task Checks_wait_for_the_lines_to_settle_and_run_at_most_once_a_minute()
    {
        var log = new JsonLinesPlayLog(_folder, NullLogger<JsonLinesPlayLog>.Instance);
        var notifications = new FakeNotifications();
        var time = new ManualTimeProvider();
        var file = Path.Combine(_folder, AchievementWatcher.FileName);
        using var watcher = new AchievementWatcher(log, notifications, file, time, NullLogger<AchievementWatcher>.Instance);
        await watcher.StartAsync(Ct);

        await log.AppendAsync(Song("first", "a", Day, 60, 200), Ct);
        Assert.False(File.Exists(file));
        time.Advance(AchievementWatcher.SettleDelay);
        await WaitForAsync(() => File.Exists(file));

        // Ten hours right after: the next check comes a minute after the first one.
        await log.AppendAsync(Song("ten", "b", Day.AddMinutes(5), 10 * 3600, null), Ct);
        time.Advance(TimeSpan.FromSeconds(30));
        await Task.Delay(100, Ct);
        Assert.Empty(notifications.Shown);
        time.Advance(TimeSpan.FromSeconds(30));
        await WaitForAsync(() => notifications.Shown.Count == 1);
        Assert.Equal("10 hours, Marathon", notifications.Shown[0].Message);

        await watcher.StopAsync(Ct);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(10, Ct);
        }

        Assert.True(condition());
    }

    private static AchievementReport Report(IEnumerable<PlayRecord> records) =>
        Achievements.Compute(records, new DateOnly(2026, 6, 1), TimeZoneInfo.Utc);

    private static Achievement Badge(AchievementReport report, string id) => report.Badges.Single(b => b.Goal.Id == id);

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
}
