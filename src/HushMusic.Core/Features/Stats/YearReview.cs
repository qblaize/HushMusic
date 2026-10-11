namespace HushMusic.Core.Features.Stats;

/// <summary>A calendar year in the play log.</summary>
/// <param name="Plays">Songs listened to long enough to count as a play.</param>
public sealed record ListeningYear(int Year, int Plays, TimeSpan Listened)
{
    /// <summary>The cover of the year's most played song.</summary>
    public string? ArtUrl { get; init; }

    /// <summary>Enough plays for "Your year in Hush".</summary>
    public bool HasReview => Plays >= YearReview.MinimumPlays;
}

/// <summary>"Your year in Hush": one calendar year of listening (the current one so far), from the local play log.</summary>
public sealed record YearReview
{
    /// <summary>Fewer plays than this make too thin a story to tell.</summary>
    public const int MinimumPlays = 10;

    public const int DefaultTopCount = 5;

    public required int Year { get; init; }

    /// <summary>The year isn't over yet: the review covers it up to today.</summary>
    public bool IsInProgress { get; init; }

    /// <summary>January 1st.</summary>
    public DateOnly FirstDay { get; init; }

    /// <summary>December 31st, or today while the year goes on.</summary>
    public DateOnly LastDay { get; init; }

    public TimeSpan TotalListened => MusicListened + RadioListened;

    public TimeSpan MusicListened { get; init; }

    public TimeSpan RadioListened { get; init; }

    /// <summary>Songs that were listened to long enough to count as a play.</summary>
    public int Plays { get; init; }

    public int DistinctSongs { get; init; }

    public int DistinctArtists { get; init; }

    /// <summary>Radio stations listened to long enough to count.</summary>
    public int Stations { get; init; }

    public IReadOnlyList<TopSong> TopSongs { get; init; } = [];

    public IReadOnlyList<TopArtist> TopArtists { get; init; } = [];

    public IReadOnlyList<TopAlbum> TopAlbums { get; init; } = [];

    public IReadOnlyList<TopStation> TopStations { get; init; } = [];

    /// <summary>Listening time by hour of the day (local time), 24 entries from midnight.</summary>
    public IReadOnlyList<TimeSpan> ByHour { get; init; } = new TimeSpan[24];

    /// <summary>Listening time by day of the week (local time), 7 entries from Monday.</summary>
    public IReadOnlyList<TimeSpan> ByWeekday { get; init; } = new TimeSpan[7];

    /// <summary>Listening time by month, 12 entries from January (by the day each play started).</summary>
    public IReadOnlyList<TimeSpan> ByMonth { get; init; } = new TimeSpan[12];

    /// <summary>Listening time on each day of the year, from January 1st (by the day each play started).</summary>
    public IReadOnlyList<TimeSpan> ByDay { get; init; } = [];

    /// <summary>Days with at least one play (a song or a radio station listened to long enough to count).</summary>
    public int DaysListened { get; init; }

    public Streak? LongestStreak { get; init; }

    /// <summary>The day with the most listening.</summary>
    public DateOnly? BiggestDay { get; init; }

    public TimeSpan BiggestDayListened { get; init; }

    /// <summary>The hour of the day (0–23) with the most listening.</summary>
    public int? PeakHour { get; init; }

    public DayOfWeek? PeakWeekday { get; init; }

    /// <summary>The month (1–12) with the most listening.</summary>
    public int? PeakMonth { get; init; }

    /// <summary>Days from January 1st up to <see cref="LastDay"/>.</summary>
    public int DaysCovered => LastDay.DayNumber - FirstDay.DayNumber + 1;

    public bool HasEnoughData => Plays >= MinimumPlays;
}

/// <summary>Turns play records into a <see cref="YearReview"/>. Pure: no I/O, no clock.</summary>
public static class YearReviewCalculator
{
    /// <summary>The first moment of <paramref name="year"/> in <paramref name="timeZone"/>.</summary>
    public static DateTimeOffset YearStart(int year, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        var midnight = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(midnight, timeZone.GetUtcOffset(midnight));
    }

    /// <summary>Every year with listening, newest first.</summary>
    public static IReadOnlyList<ListeningYear> Years(IEnumerable<PlayRecord> records, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(timeZone);
        return
        [
            .. records
                .Where(r => r.ListenedSeconds > 0)
                .GroupBy(r => ListeningDays.LocalStart(r, timeZone).Year)
                .Select(g => new ListeningYear(g.Key, g.Count(r => r.Kind == PlayKind.Track && r.IsPlay), ListeningStatsCalculator.Sum(g))
                {
                    ArtUrl = ListeningStatsCalculator.TopSongs([.. g.Where(r => r.Kind == PlayKind.Track)], 1).FirstOrDefault()?.ArtUrl,
                })
                .OrderByDescending(y => y.Year),
        ];
    }

    /// <param name="records">One record per play (as <see cref="IPlayLog.ReadAsync"/> returns them); other years are ignored.</param>
    /// <param name="now">Plays after it are ignored, and a year that hasn't ended is covered up to its day.</param>
    public static YearReview Compute(
        IEnumerable<PlayRecord> records,
        int year,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        int topCount = YearReview.DefaultTopCount)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(timeZone);

        var firstDay = new DateOnly(year, 1, 1);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, timeZone).DateTime);
        var inProgress = today.Year == year;
        var lastDay = inProgress ? today : new DateOnly(year, 12, 31);
        var inYear = records
            .Where(r => r.ListenedSeconds > 0 && r.Start <= now && ListeningDays.LocalStart(r, timeZone).Year == year)
            .ToList();

        var tracks = inYear.Where(r => r.Kind == PlayKind.Track).ToList();
        var radio = inYear.Where(r => r.Kind == PlayKind.Radio).ToList();
        var counted = tracks.Where(r => r.IsPlay).ToList();

        var byHour = new TimeSpan[24];
        var byWeekday = new TimeSpan[7];
        var byMonth = new TimeSpan[12];
        var byDay = new TimeSpan[new DateOnly(year, 12, 31).DayOfYear];
        var playedDays = new HashSet<DateOnly>();
        foreach (var record in inYear)
        {
            ListeningStatsCalculator.Spread(record, timeZone, byHour, byWeekday);
            var day = ListeningDays.DayOf(record, timeZone);
            byMonth[day.Month - 1] += record.Listened;
            byDay[day.DayOfYear - 1] += record.Listened;
            if (record.IsPlay)
            {
                playedDays.Add(day);
            }
        }

        var biggest = byDay.Select((time, index) => (Time: time, Index: index)).MaxBy(d => d.Time);
        return new YearReview
        {
            Year = year,
            IsInProgress = inProgress,
            FirstDay = firstDay,
            LastDay = lastDay < firstDay ? firstDay : lastDay,
            MusicListened = ListeningStatsCalculator.Sum(tracks),
            RadioListened = ListeningStatsCalculator.Sum(radio),
            Plays = counted.Count,
            DistinctSongs = counted.Select(r => r.ItemId).Distinct(StringComparer.Ordinal).Count(),
            DistinctArtists = counted.SelectMany(r => r.Artists).Select(ListeningStatsCalculator.ArtistKey).Distinct(StringComparer.Ordinal).Count(),
            Stations = radio.Where(r => r.IsPlay).Select(r => r.ItemId).Distinct(StringComparer.Ordinal).Count(),
            TopSongs = ListeningStatsCalculator.TopSongs(tracks, topCount),
            TopArtists = ListeningStatsCalculator.TopArtists(tracks, topCount),
            TopAlbums = ListeningStatsCalculator.TopAlbums(tracks, topCount),
            TopStations = ListeningStatsCalculator.TopStations(radio, topCount),
            ByHour = byHour,
            ByWeekday = byWeekday,
            ByMonth = byMonth,
            ByDay = byDay,
            DaysListened = playedDays.Count,
            LongestStreak = ListeningDays.Longest(playedDays),
            BiggestDay = biggest.Time > TimeSpan.Zero ? firstDay.AddDays(biggest.Index) : null,
            BiggestDayListened = biggest.Time,
            PeakHour = Peak(byHour),
            PeakWeekday = Peak(byWeekday) is { } monday ? (DayOfWeek)((monday + 1) % 7) : null,
            PeakMonth = Peak(byMonth) is { } month ? month + 1 : null,
        };
    }

    // The index of the largest entry (the first of equal ones); null when there is no listening at all.
    private static int? Peak(TimeSpan[] values)
    {
        var max = values.Max();
        return max > TimeSpan.Zero ? Array.IndexOf(values, max) : null;
    }
}
