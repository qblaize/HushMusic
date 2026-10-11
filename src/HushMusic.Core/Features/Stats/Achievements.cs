namespace HushMusic.Core.Features.Stats;

public enum AchievementKind
{
    /// <summary>Hours of listening, music and radio together.</summary>
    ListeningHours,

    /// <summary>Days in a row with at least one play (the longest run so far).</summary>
    Streak,

    /// <summary>Different songs played.</summary>
    Songs,

    /// <summary>Different artists played.</summary>
    Artists,

    /// <summary>Hours of listening in a single day.</summary>
    Marathon,

    /// <summary>Songs played that started between midnight and 4 a.m.</summary>
    NightOwl,

    /// <summary>Songs played that started between 5 and 8 a.m.</summary>
    EarlyBird,

    /// <summary>Hours of live radio.</summary>
    RadioHours,
}

/// <summary>
/// One badge: a target for one kind of listening. <see cref="Id"/> is stored with the badges already announced, so it
/// never changes.
/// </summary>
public sealed record AchievementGoal(string Id, AchievementKind Kind, int Target, string Title);

/// <summary>A badge and how far along it is.</summary>
/// <param name="Progress">In the unit of the goal's target (hours, days, songs, artists, plays).</param>
/// <param name="UnlockedOn">The day the target was reached; null while it is locked.</param>
public sealed record Achievement(AchievementGoal Goal, long Progress, DateOnly? UnlockedOn)
{
    public bool IsUnlocked => UnlockedOn is not null;

    /// <summary>0 to 1.</summary>
    public double Ratio => IsUnlocked ? 1 : Math.Clamp((double)Progress / Goal.Target, 0, 1);
}

/// <summary>Every badge (in <see cref="Achievements.Goals"/> order) and the streaks they are based on.</summary>
public sealed record AchievementReport(IReadOnlyList<Achievement> Badges, Streak? CurrentStreak, Streak? LongestStreak)
{
    public int Unlocked => Badges.Count(b => b.IsUnlocked);
}

/// <summary>The badges of "Your stats", all computed from the local play log (so they also count what was played before).</summary>
public static class Achievements
{
    /// <summary>Night owl: songs started from midnight up to (not including) this hour.</summary>
    public const int NightEndsAt = 4;

    /// <summary>Early bird: songs started from this hour up to (not including) <see cref="MorningEndsAt"/>.</summary>
    public const int MorningStartsAt = 5;

    public const int MorningEndsAt = 8;

    public static IReadOnlyList<AchievementGoal> Goals { get; } =
    [
        new("hours-10", AchievementKind.ListeningHours, 10, "10 hours"),
        new("hours-100", AchievementKind.ListeningHours, 100, "100 hours"),
        new("hours-500", AchievementKind.ListeningHours, 500, "500 hours"),
        new("hours-1000", AchievementKind.ListeningHours, 1000, "1,000 hours"),
        new("streak-7", AchievementKind.Streak, 7, "7-day streak"),
        new("streak-30", AchievementKind.Streak, 30, "30-day streak"),
        new("streak-100", AchievementKind.Streak, 100, "100-day streak"),
        new("songs-100", AchievementKind.Songs, 100, "100 songs"),
        new("songs-1000", AchievementKind.Songs, 1000, "1,000 songs"),
        new("songs-5000", AchievementKind.Songs, 5000, "5,000 songs"),
        new("artists-50", AchievementKind.Artists, 50, "50 artists"),
        new("artists-250", AchievementKind.Artists, 250, "250 artists"),
        new("artists-1000", AchievementKind.Artists, 1000, "1,000 artists"),
        new("marathon", AchievementKind.Marathon, 3, "Marathon"),
        new("night-owl", AchievementKind.NightOwl, 50, "Night owl"),
        new("early-bird", AchievementKind.EarlyBird, 50, "Early bird"),
        new("radio-10", AchievementKind.RadioHours, 10, "10 hours of radio"),
        new("radio-100", AchievementKind.RadioHours, 100, "100 hours of radio"),
    ];

    /// <summary>The badges for <paramref name="records"/>, as of <paramref name="today"/> (for the current streak).</summary>
    public static AchievementReport Compute(IEnumerable<PlayRecord> records, DateOnly today, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(records);
        var tally = new AchievementTally(timeZone);
        foreach (var record in records.OrderBy(r => r.Start))
        {
            tally.Add(record);
        }

        return tally.Report(today);
    }
}

/// <summary>
/// The running totals behind the badges. Lines can be added one at a time as they are written: a later line of the same
/// play replaces the earlier one (only the extra listening counts). Added in the order plays started, each badge's
/// unlock day is the day it was reached. Not thread-safe.
/// </summary>
public sealed class AchievementTally(TimeZoneInfo timeZone)
{
    private readonly Dictionary<string, Entry> _plays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _songPlays = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _artistPlays = new(StringComparer.Ordinal);
    private readonly Dictionary<DateOnly, long> _daySeconds = [];
    private readonly HashSet<DateOnly> _playedDays = [];
    private readonly Dictionary<string, DateOnly> _unlocked = new(StringComparer.Ordinal);
    private long _seconds;
    private long _radioSeconds;
    private long _bestDaySeconds;
    private int _nightPlays;
    private int _morningPlays;
    private Streak? _longest;

    /// <summary>The ids of the badges unlocked so far.</summary>
    public IReadOnlyCollection<string> UnlockedIds => _unlocked.Keys;

    public void Add(PlayRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.ListenedSeconds <= 0)
        {
            return;
        }

        var known = _plays.GetValueOrDefault(record.Id);
        if (known is not null && record.ListenedSeconds <= known.Listened)
        {
            // An earlier line of a play already counted.
            return;
        }

        var start = ListeningDays.LocalStart(record, timeZone);
        var day = DateOnly.FromDateTime(start.DateTime);
        var extra = record.ListenedSeconds - (known?.Listened ?? 0);
        var newlyCounted = record.IsPlay && known?.Counted != true;
        _plays[record.Id] = new Entry(record.ListenedSeconds, record.IsPlay || known?.Counted == true);

        _seconds += extra;
        if (record.Kind == PlayKind.Radio)
        {
            _radioSeconds += extra;
        }

        var daySeconds = _daySeconds.GetValueOrDefault(day) + extra;
        _daySeconds[day] = daySeconds;
        _bestDaySeconds = Math.Max(_bestDaySeconds, daySeconds);

        if (newlyCounted)
        {
            if (record.Kind == PlayKind.Track)
            {
                Count(_songPlays, record.ItemId);
                foreach (var key in record.Artists.Select(ListeningStatsCalculator.ArtistKey).Distinct(StringComparer.Ordinal))
                {
                    Count(_artistPlays, key);
                }

                if (start.Hour < Achievements.NightEndsAt)
                {
                    _nightPlays++;
                }
                else if (start.Hour is >= Achievements.MorningStartsAt and < Achievements.MorningEndsAt)
                {
                    _morningPlays++;
                }
            }

            if (_playedDays.Add(day))
            {
                ExtendStreak(day);
            }
        }

        // Hours are reached as the play ends; everything else on the day it started.
        var reachedOn = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(record.Start + record.Listened, timeZone).DateTime);
        foreach (var goal in Achievements.Goals)
        {
            if (!_unlocked.ContainsKey(goal.Id) && Progress(goal.Kind) >= goal.Target)
            {
                _unlocked[goal.Id] = goal.Kind switch
                {
                    AchievementKind.ListeningHours or AchievementKind.RadioHours => reachedOn,
                    AchievementKind.Streak when _longest is { } streak => streak.First.AddDays(goal.Target - 1),
                    _ => day,
                };
            }
        }
    }

    public AchievementReport Report(DateOnly today) => new(
        [.. Achievements.Goals.Select(g => new Achievement(g, Progress(g.Kind), _unlocked.TryGetValue(g.Id, out var on) ? on : null))],
        ListeningDays.Current(_playedDays, today),
        _longest);

    private long Progress(AchievementKind kind) => kind switch
    {
        AchievementKind.ListeningHours => _seconds / 3600,
        AchievementKind.Streak => _longest?.Days ?? 0,
        AchievementKind.Songs => _songPlays.Count,
        AchievementKind.Artists => _artistPlays.Count,
        AchievementKind.Marathon => _bestDaySeconds / 3600,
        AchievementKind.NightOwl => _nightPlays,
        AchievementKind.EarlyBird => _morningPlays,
        AchievementKind.RadioHours => _radioSeconds / 3600,
        _ => 0,
    };

    private static void Count(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;

    // A newly played day joins the days around it into one run.
    private void ExtendStreak(DateOnly day)
    {
        var first = day;
        while (_playedDays.Contains(first.AddDays(-1)))
        {
            first = first.AddDays(-1);
        }

        var last = day;
        while (_playedDays.Contains(last.AddDays(1)))
        {
            last = last.AddDays(1);
        }

        var run = new Streak(first, last);
        if (_longest is null || run.Days > _longest.Value.Days)
        {
            _longest = run;
        }
    }

    private sealed record Entry(int Listened, bool Counted);
}
