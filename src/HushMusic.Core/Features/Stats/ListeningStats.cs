namespace HushMusic.Core.Features.Stats;

public enum StatsPeriod
{
    /// <summary>The last 7 days.</summary>
    Week,

    /// <summary>The last 30 days.</summary>
    Month,

    /// <summary>The last 365 days.</summary>
    Year,

    AllTime,
}

public sealed record TopSong(string VideoId, string Title, string Artists, string? AlbumId, string? ArtUrl, int Plays, TimeSpan Listened);

public sealed record TopArtist(string Name, string? BrowseId, string? ArtUrl, int Plays, TimeSpan Listened);

public sealed record TopAlbum(string Title, string? BrowseId, string Artists, string? ArtUrl, int Plays, TimeSpan Listened);

/// <summary>A radio station: <paramref name="Sessions"/> counts the times it was listened to long enough to count.</summary>
public sealed record TopStation(string StationId, string Name, string? ArtUrl, int Sessions, TimeSpan Listened);

/// <summary>Listening statistics for one period, computed from the local play log.</summary>
public sealed record ListeningSummary
{
    public required StatsPeriod Period { get; init; }

    /// <summary>The start of the period; null for all time.</summary>
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset To { get; init; }

    /// <summary>Music and radio together.</summary>
    public TimeSpan TotalListened => MusicListened + RadioListened;

    public TimeSpan MusicListened { get; init; }

    public TimeSpan RadioListened { get; init; }

    /// <summary>Songs that were listened to long enough to count as a play.</summary>
    public int Plays { get; init; }

    public int DistinctSongs { get; init; }

    public int DistinctArtists { get; init; }

    public IReadOnlyList<TopSong> TopSongs { get; init; } = [];

    public IReadOnlyList<TopArtist> TopArtists { get; init; } = [];

    public IReadOnlyList<TopAlbum> TopAlbums { get; init; } = [];

    public IReadOnlyList<TopStation> TopStations { get; init; } = [];

    /// <summary>Listening time by hour of the day (local time), 24 entries from midnight.</summary>
    public IReadOnlyList<TimeSpan> ByHour { get; init; } = new TimeSpan[24];

    /// <summary>Listening time by day of the week (local time), 7 entries from Monday.</summary>
    public IReadOnlyList<TimeSpan> ByWeekday { get; init; } = new TimeSpan[7];

    public bool IsEmpty => TotalListened <= TimeSpan.Zero && Plays == 0;
}

/// <summary>Turns play records into a <see cref="ListeningSummary"/>. Pure: no I/O, no clock.</summary>
public static class ListeningStatsCalculator
{
    public const int DefaultTopCount = 10;

    /// <summary>The first moment of <paramref name="period"/> that ends at <paramref name="now"/>; null for all time.</summary>
    public static DateTimeOffset? PeriodStart(StatsPeriod period, DateTimeOffset now) => period switch
    {
        StatsPeriod.Week => now.AddDays(-7),
        StatsPeriod.Month => now.AddDays(-30),
        StatsPeriod.Year => now.AddDays(-365),
        _ => null,
    };

    /// <param name="records">One record per play (as <see cref="IPlayLog.ReadAsync"/> returns them).</param>
    /// <param name="timeZone">For the hour-of-day and weekday charts.</param>
    public static ListeningSummary Compute(
        IEnumerable<PlayRecord> records,
        StatsPeriod period,
        DateTimeOffset now,
        TimeZoneInfo timeZone,
        int topCount = DefaultTopCount)
    {
        ArgumentNullException.ThrowIfNull(records);
        ArgumentNullException.ThrowIfNull(timeZone);

        var from = PeriodStart(period, now);
        var inPeriod = records.Where(r => (from is null || r.Start >= from) && r.Start <= now && r.ListenedSeconds > 0).ToList();

        var tracks = inPeriod.Where(r => r.Kind == PlayKind.Track).ToList();
        var radio = inPeriod.Where(r => r.Kind == PlayKind.Radio).ToList();

        var byHour = new TimeSpan[24];
        var byWeekday = new TimeSpan[7];
        foreach (var record in inPeriod)
        {
            Spread(record, timeZone, byHour, byWeekday);
        }

        return new ListeningSummary
        {
            Period = period,
            From = from,
            To = now,
            MusicListened = Sum(tracks),
            RadioListened = Sum(radio),
            Plays = tracks.Count(r => r.IsPlay),
            DistinctSongs = tracks.Where(r => r.IsPlay).Select(r => r.ItemId).Distinct(StringComparer.Ordinal).Count(),
            DistinctArtists = tracks.Where(r => r.IsPlay).SelectMany(r => r.Artists).Select(ArtistKey).Distinct(StringComparer.Ordinal).Count(),
            TopSongs = TopSongs(tracks, topCount),
            TopArtists = TopArtists(tracks, topCount),
            TopAlbums = TopAlbums(tracks, topCount),
            TopStations = TopStations(radio, topCount),
            ByHour = byHour,
            ByWeekday = byWeekday,
        };
    }

    private static TimeSpan Sum(IEnumerable<PlayRecord> records) => TimeSpan.FromSeconds(records.Sum(r => (long)r.ListenedSeconds));

    // Ranked by plays, then by listening time; groups with no counted play only show up when there is room.
    private static List<TopSong> TopSongs(List<PlayRecord> tracks, int count) =>
    [
        .. tracks
            .GroupBy(r => r.ItemId, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.Start)!;
                return new TopSong(
                    g.Key,
                    latest.Title,
                    string.Join(", ", latest.Artists.Select(a => a.Name)),
                    latest.AlbumId,
                    Art(g),
                    g.Count(r => r.IsPlay),
                    Sum(g));
            })
            .Where(s => s.Plays > 0)
            .OrderByDescending(s => s.Plays)
            .ThenByDescending(s => s.Listened)
            .ThenBy(s => s.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(count),
    ];

    // A song with several artists counts for each of them.
    private static List<TopArtist> TopArtists(List<PlayRecord> tracks, int count) =>
    [
        .. tracks
            .SelectMany(r => r.Artists.Select(a => (Artist: a, Record: r)))
            .GroupBy(x => ArtistKey(x.Artist), StringComparer.Ordinal)
            .Select(g =>
            {
                var records = g.Select(x => x.Record).ToList();
                var name = g.Select(x => x.Artist).MaxBy(a => a.BrowseId is null ? 0 : 1)!;
                var topSong = records.Where(r => r.IsPlay).GroupBy(r => r.ItemId).MaxBy(s => s.Count());
                return new TopArtist(
                    g.Select(x => x.Artist.Name).GroupBy(n => n).MaxBy(n => n.Count())!.Key,
                    name.BrowseId,
                    topSong is null ? Art(records) : Art(topSong),
                    records.Count(r => r.IsPlay),
                    Sum(records));
            })
            .Where(a => a.Plays > 0)
            .OrderByDescending(a => a.Plays)
            .ThenByDescending(a => a.Listened)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(count),
    ];

    private static List<TopAlbum> TopAlbums(List<PlayRecord> tracks, int count) =>
    [
        .. tracks
            .Where(r => r.Album is not null)
            .GroupBy(r => r.AlbumId ?? "name:" + r.Album!.ToUpperInvariant() + "|" + (r.Artists.FirstOrDefault()?.Name.ToUpperInvariant() ?? string.Empty), StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.Start)!;
                return new TopAlbum(
                    latest.Album!,
                    latest.AlbumId,
                    latest.Artists.FirstOrDefault()?.Name ?? string.Empty,
                    Art(g),
                    g.Count(r => r.IsPlay),
                    Sum(g));
            })
            .Where(a => a.Plays > 0)
            .OrderByDescending(a => a.Plays)
            .ThenByDescending(a => a.Listened)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .Take(count),
    ];

    // Stations are ranked by time: a radio "play" is an evening, not a song.
    private static List<TopStation> TopStations(List<PlayRecord> radio, int count) =>
    [
        .. radio
            .GroupBy(r => r.ItemId, StringComparer.Ordinal)
            .Select(g =>
            {
                var latest = g.MaxBy(r => r.Start)!;
                return new TopStation(g.Key, latest.Title, Art(g), g.Count(r => r.IsPlay), Sum(g));
            })
            .OrderByDescending(s => s.Listened)
            .ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(count),
    ];

    private static string ArtistKey(PlayArtist artist) => artist.BrowseId ?? "name:" + artist.Name.ToUpperInvariant();

    private static string? Art(IEnumerable<PlayRecord> records) =>
        records.Where(r => r.ArtUrl is not null).MaxBy(r => r.Start)?.ArtUrl;

    // A long session (an evening of radio) is spread over the hours and days it covered, as if listened to without pauses.
    private static void Spread(PlayRecord record, TimeZoneInfo timeZone, TimeSpan[] byHour, TimeSpan[] byWeekday)
    {
        var cursor = TimeZoneInfo.ConvertTime(record.Start, timeZone);
        var left = record.Listened;
        while (left > TimeSpan.Zero)
        {
            var nextHour = new DateTimeOffset(cursor.Year, cursor.Month, cursor.Day, cursor.Hour, 0, 0, cursor.Offset).AddHours(1);
            var slice = nextHour - cursor;
            if (slice <= TimeSpan.Zero || slice > left)
            {
                slice = left;
            }

            byHour[cursor.Hour] += slice;
            byWeekday[((int)cursor.DayOfWeek + 6) % 7] += slice;
            left -= slice;
            cursor = TimeZoneInfo.ConvertTime(cursor + slice, timeZone);
        }
    }
}
