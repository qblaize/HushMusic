namespace HushMusic.Core.Features.Stats;

/// <summary>A run of days in a row with at least one play.</summary>
public readonly record struct Streak(DateOnly First, DateOnly Last)
{
    public int Days => Last.DayNumber - First.DayNumber + 1;
}

/// <summary>Days of listening: which local day a play belongs to, and streaks of days in a row. Pure.</summary>
public static class ListeningDays
{
    /// <summary>The local day the play started on (a session past midnight belongs to the day it started).</summary>
    public static DateOnly DayOf(PlayRecord record, TimeZoneInfo timeZone) => DateOnly.FromDateTime(LocalStart(record, timeZone).DateTime);

    public static DateTimeOffset LocalStart(PlayRecord record, TimeZoneInfo timeZone) => TimeZoneInfo.ConvertTime(record.Start, timeZone);

    /// <summary>The longest run of days in a row (the earliest of equally long ones); null without days.</summary>
    public static Streak? Longest(IEnumerable<DateOnly> days)
    {
        Streak? longest = null;
        Streak? run = null;
        foreach (var day in days.Distinct().Order())
        {
            run = run is { } current && current.Last.AddDays(1) == day ? current with { Last = day } : new Streak(day, day);
            if (longest is null || run.Value.Days > longest.Value.Days)
            {
                longest = run;
            }
        }

        return longest;
    }

    /// <summary>
    /// The run that reaches today, or yesterday while nothing has played yet today (the streak goes on until the day
    /// ends without a play); null when it is broken.
    /// </summary>
    public static Streak? Current(IReadOnlySet<DateOnly> days, DateOnly today)
    {
        var last = days.Contains(today) ? today : today.AddDays(-1);
        if (!days.Contains(last))
        {
            return null;
        }

        var first = last;
        while (days.Contains(first.AddDays(-1)))
        {
            first = first.AddDays(-1);
        }

        return new Streak(first, last);
    }
}
