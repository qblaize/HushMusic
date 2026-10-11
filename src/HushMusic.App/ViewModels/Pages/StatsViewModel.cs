using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.App.ViewModels.YearInReview;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>A headline number at the top of the stats page.</summary>
public sealed record StatsTile(string Label, string Value, string Caption);

/// <summary>One entry of a top list (song, artist, album or station), ready to show.</summary>
public sealed class StatsRankItem(int rank, string title, string subtitle, string detail, string? artUrl, Action? open)
{
    public string Rank { get; } = rank.ToString(CultureInfo.CurrentCulture);

    public string Title { get; } = title;

    public string Subtitle { get; } = subtitle;

    /// <summary>"12 plays · 48 min".</summary>
    public string Detail { get; } = detail;

    public string? ArtUrl { get; } = artUrl;

    public string AutomationName => string.IsNullOrEmpty(Subtitle) ? $"{Rank}. {Title}, {Detail}" : $"{Rank}. {Title}, {Subtitle}, {Detail}";

    public void Open() => open?.Invoke();
}

/// <summary>A year to pick for "Your year in Hush" (when there is more than one).</summary>
public sealed record ReviewYearChoice(int Year, bool IsSelected)
{
    public string Label => Year.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A badge of the achievements section, ready to show.</summary>
public sealed class AchievementItem(Achievement achievement)
{
    private readonly AchievementGoal _goal = achievement.Goal;

    public string Title => _goal.Title;

    public bool IsUnlocked => achievement.IsUnlocked;

    public bool IsLocked => !achievement.IsUnlocked;

    public string Glyph => _goal.Kind switch
    {
        AchievementKind.ListeningHours => "",
        AchievementKind.Streak => "",
        AchievementKind.Songs => "",
        AchievementKind.Artists => "",
        AchievementKind.Marathon => "",
        AchievementKind.NightOwl => "",
        AchievementKind.EarlyBird => "",
        _ => "",
    };

    /// <summary>Unlocked: the day ("Mar 3, 2026"). Locked: how far along it is ("62 / 100 hours").</summary>
    public string Caption => achievement.UnlockedOn is { } day
        ? day.ToString("MMM d, yyyy", CultureInfo.CurrentCulture)
        : _goal.Kind switch
        {
            AchievementKind.ListeningHours or AchievementKind.RadioHours => Progress("hours"),
            AchievementKind.Streak => Progress("days in a row"),
            AchievementKind.Songs => Progress("songs"),
            AchievementKind.Artists => Progress("artists"),
            AchievementKind.Marathon => Progress("hours in a day"),
            AchievementKind.NightOwl => Progress("late-night songs"),
            AchievementKind.EarlyBird => Progress("early-morning songs"),
            _ => Progress(string.Empty),
        };

    /// <summary>What it takes, for the tooltip.</summary>
    public string Description => _goal.Kind switch
    {
        AchievementKind.ListeningHours => $"Listen for {Target} hours, music and radio together",
        AchievementKind.Streak => $"Listen on {Target} days in a row",
        AchievementKind.Songs => $"Play {Target} different songs",
        AchievementKind.Artists => $"Play songs by {Target} different artists",
        AchievementKind.Marathon => $"Listen for {Target} hours in a single day",
        AchievementKind.NightOwl => $"Play {Target} songs between midnight and 4 AM",
        AchievementKind.EarlyBird => $"Play {Target} songs between 5 and 8 AM",
        _ => $"Listen to {Target} hours of live radio",
    };

    public string AutomationName => IsUnlocked ? $"{Title}, unlocked {Caption}. {Description}." : $"{Title}, locked, {Caption}. {Description}.";

    /// <summary>Locked badges are dimmed.</summary>
    public double TitleOpacity => IsUnlocked ? 1 : 0.6;

    public GridLength ProgressDone => new(Math.Max(achievement.Ratio, 0.0001), GridUnitType.Star);

    public GridLength ProgressLeft => new(Math.Max(1 - achievement.Ratio, 0.0001), GridUnitType.Star);

    private string Target => _goal.Target.ToString("N0", CultureInfo.CurrentCulture);

    private string Progress(string unit) =>
        $"{Math.Min(achievement.Progress, _goal.Target).ToString("N0", CultureInfo.CurrentCulture)} / {Target} {unit}".TrimEnd();
}

/// <summary>"Your stats": what was listened to in a period, from the local play log. Nothing here needs an account.</summary>
public sealed partial class StatsViewModel(
    IListeningStats stats,
    IConfirmDialogService confirm,
    YearInReviewViewModel yearInReview,
    PageServices services) : PageViewModelBase(services)
{
    private const int TopArtistCount = 6;
    private const int TopSongCount = 10;
    private const int TopAlbumCount = 6;
    private const int TopStationCount = 5;

    private static readonly TimeSpan RefreshDelay = TimeSpan.FromSeconds(2);

    // The years and the badges need the whole log: read again at most this often while the page is open.
    private static readonly TimeSpan HighlightsRefreshInterval = TimeSpan.FromMinutes(1);

    private int _refreshQueued;
    private long? _highlightsLoadedAt;
    private IReadOnlyList<ListeningYear> _years = [];

    [ObservableProperty]
    public partial StatsPeriod Period { get; set; } = StatsPeriod.Month;

    /// <summary>"Past 30 days".</summary>
    [ObservableProperty]
    public partial string PeriodCaption { get; set; } = string.Empty;

    [ObservableProperty]
    public partial IReadOnlyList<StatsTile> Tiles { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopArtists))]
    public partial IReadOnlyList<StatsRankItem> TopArtists { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopSongs))]
    public partial IReadOnlyList<StatsRankItem> TopSongs { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopAlbums))]
    public partial IReadOnlyList<StatsRankItem> TopAlbums { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTopStations))]
    public partial IReadOnlyList<StatsRankItem> TopStations { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> ByHour { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<ChartBar> ByWeekday { get; set; } = [];

    /// <summary>The listening-by-hour chart's title, with the busiest hour.</summary>
    [ObservableProperty]
    public partial string HourSummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string WeekdaySummary { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EmptyMessage { get; set; } = string.Empty;

    /// <summary>Anything was ever played: the year card and the achievements show.</summary>
    [ObservableProperty]
    public partial bool HasHighlights { get; set; }

    [ObservableProperty]
    public partial int ReviewYear { get; set; }

    /// <summary>"Your 2026 in Hush".</summary>
    [ObservableProperty]
    public partial string ReviewTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReviewCaption { get; set; } = string.Empty;

    /// <summary>The year's top song, behind the card.</summary>
    [ObservableProperty]
    public partial string? ReviewArtUrl { get; set; }

    /// <summary>The year has enough plays for its story.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenReviewCommand))]
    public partial bool CanOpenReview { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasYearChoice))]
    public partial IReadOnlyList<ReviewYearChoice> ReviewYears { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<AchievementItem> Achievements { get; set; } = [];

    /// <summary>"5 of 18 unlocked · Current streak: 3 days · Longest: 12 days".</summary>
    [ObservableProperty]
    public partial string AchievementsSummary { get; set; } = string.Empty;

    public bool HasYearChoice => ReviewYears.Count > 1;

    public bool HasTopArtists => TopArtists.Count > 0;

    public bool HasTopSongs => TopSongs.Count > 0;

    public bool HasTopAlbums => TopAlbums.Count > 0;

    public bool HasTopStations => TopStations.Count > 0;

    /// <summary>"12 h 40 min", "48 min", "Under a minute".</summary>
    public static string FormatListened(TimeSpan time)
    {
        var minutes = (long)Math.Round(time.TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes < 1)
        {
            return time > TimeSpan.Zero ? "Under a minute" : "0 min";
        }

        var hours = minutes / 60;
        var rest = minutes % 60;
        return hours == 0 ? $"{rest} min" : rest == 0 ? $"{hours:N0} h" : $"{hours:N0} h {rest} min";
    }

    public static string FormatPlays(int plays) => plays == 1 ? "1 play" : $"{plays:N0} plays";

    public static string Caption(StatsPeriod period) => period switch
    {
        StatsPeriod.Week => "Past 7 days",
        StatsPeriod.Month => "Past 30 days",
        StatsPeriod.Year => "Past 12 months",
        _ => "All time",
    };

    public void SelectPeriod(StatsPeriod period)
    {
        if (period == Period && HasContent)
        {
            return;
        }

        Period = period;
        _ = LoadAsync();
    }

    public void SelectReviewYear(int year)
    {
        if (year != ReviewYear && _years.Any(y => y.Year == year))
        {
            ApplyReviewYear(year);
        }
    }

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        stats.Changed += OnStatsChanged;
        return Task.WhenAll(LoadAsync(), LoadHighlightsAsync(force: true));
    }

    protected override void OnNavigatedFromCore() => stats.Changed -= OnStatsChanged;

    protected override Task LoadAsync()
    {
        var period = Period;
        PeriodCaption = Caption(period);
        return RunAsync(
            async ct =>
            {
                // Reading a year of plays is file work: keep it off the UI thread.
                var summary = await Task.Run(() => stats.GetSummaryAsync(period, ct), ct);
                if (period == Period)
                {
                    Apply(summary);
                    HasContent = true;
                }
            },
            "Couldn't load your stats");
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        var confirmed = await confirm.ConfirmAsync(
            "Clear your stats?",
            "Every play Hush has recorded on this PC will be deleted. Your YouTube Music history isn't affected.",
            "Clear");
        if (!confirmed)
        {
            return;
        }

        try
        {
            await stats.ClearAsync();
            Notifications.Show(new AppNotification(NotificationSeverity.Success, "Stats cleared", "Hush starts counting again from your next song."));
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't clear your stats", ex);
        }

        await Task.WhenAll(LoadAsync(), LoadHighlightsAsync(force: true));
    }

    [RelayCommand]
    private void OpenRankItem(StatsRankItem? item) => item?.Open();

    [RelayCommand(CanExecute = nameof(CanOpenReview))]
    private Task OpenReviewAsync() => yearInReview.OpenAsync(ReviewYear);

    // The year card and the badges: the whole log, read off the UI thread.
    private async Task LoadHighlightsAsync(bool force)
    {
        if (!force && _highlightsLoadedAt is { } loaded && Stopwatch.GetElapsedTime(loaded) < HighlightsRefreshInterval)
        {
            return;
        }

        _highlightsLoadedAt = Stopwatch.GetTimestamp();
        var token = NavigationToken;
        try
        {
            var highlights = await Task.Run(() => stats.GetHighlightsAsync(token), token);
            ApplyHighlights(highlights);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportError("Couldn't load your achievements", ex);
        }
    }

    private void ApplyHighlights(StatsHighlights highlights)
    {
        _years = highlights.Years;
        HasHighlights = _years.Count > 0;

        // The current year once it has enough plays, else the latest one that has.
        var withReview = _years.Where(y => y.HasReview).Select(y => y.Year).ToList();
        var year = withReview.Contains(ReviewYear) ? ReviewYear : withReview.Count > 0 ? withReview[0] : DateTime.Now.Year;
        ApplyReviewYear(year);

        var report = highlights.Achievements;
        Achievements = [.. report.Badges.Select(b => new AchievementItem(b))];
        List<string> summary = [$"{report.Unlocked} of {report.Badges.Count} unlocked"];
        if (report.CurrentStreak is { } current)
        {
            summary.Add($"Current streak: {Days(current.Days)}");
        }

        if (report.LongestStreak is { } longest)
        {
            summary.Add($"Longest: {Days(longest.Days)}");
        }

        AchievementsSummary = string.Join(" · ", summary);

        static string Days(int days) => days == 1 ? "1 day" : $"{days:N0} days";
    }

    private void ApplyReviewYear(int year)
    {
        var info = _years.FirstOrDefault(y => y.Year == year);
        ReviewYear = year;
        ReviewTitle = $"Your {year.ToString(CultureInfo.InvariantCulture)} in Hush";
        ReviewArtUrl = info?.ArtUrl;
        CanOpenReview = info?.HasReview == true;
        ReviewCaption = CanOpenReview
            ? year == DateTime.Now.Year
                ? "Your top songs, artists and listening time so far this year, as a short story."
                : "Your top songs, artists and listening time of the year, as a short story."
            : $"Ready once you've played {YearReview.MinimumPlays} songs this year ({(info?.Plays ?? 0).ToString("N0", CultureInfo.CurrentCulture)} so far).";

        var withReview = _years.Where(y => y.HasReview).ToList();
        ReviewYears = withReview.Count > 1 ? [.. withReview.Select(y => new ReviewYearChoice(y.Year, y.Year == year))] : [];
    }

    private void Apply(ListeningSummary summary)
    {
        IsEmpty = summary.IsEmpty;
        EmptyMessage = summary.Period == StatsPeriod.AllTime
            ? "Songs and stations you play show up here once you've listened for a bit."
            : $"Nothing played in the {Caption(summary.Period).ToLower(CultureInfo.CurrentCulture)}. Try a longer period.";

        var stations = summary.TopStations.Count;
        Tiles =
        [
            new StatsTile("Listening time", FormatListened(summary.TotalListened), PeriodCaption),
            new StatsTile("Songs played", summary.Plays.ToString("N0", CultureInfo.CurrentCulture), summary.DistinctSongs == 1 ? "1 different song" : $"{summary.DistinctSongs:N0} different songs"),
            new StatsTile("Artists", summary.DistinctArtists.ToString("N0", CultureInfo.CurrentCulture), summary.DistinctArtists == 1 ? "1 artist played" : "different artists"),
            new StatsTile("Radio", FormatListened(summary.RadioListened), stations == 1 ? "1 station" : $"{stations:N0} stations"),
        ];

        TopArtists = [.. summary.TopArtists.Take(TopArtistCount).Select((a, i) => new StatsRankItem(
            i + 1, a.Name, string.Empty, Detail(a.Plays, a.Listened), a.ArtUrl, a.BrowseId is { } id ? () => Actions.OpenArtist(id) : null))];
        TopSongs = [.. summary.TopSongs.Take(TopSongCount).Select((s, i) => new StatsRankItem(
            i + 1, s.Title, s.Artists, Detail(s.Plays, s.Listened), s.ArtUrl, () => _ = Actions.PlayTrackAsync(ToTrack(s))))];
        TopAlbums = [.. summary.TopAlbums.Take(TopAlbumCount).Select((a, i) => new StatsRankItem(
            i + 1, a.Title, a.Artists, Detail(a.Plays, a.Listened), a.ArtUrl, a.BrowseId is { } id ? () => Actions.OpenAlbum(id) : null))];
        TopStations = [.. summary.TopStations.Take(TopStationCount).Select((s, i) => new StatsRankItem(
            i + 1, s.Name, string.Empty, FormatListened(s.Listened), s.ArtUrl, () => Services.Navigation.NavigateTo(PageKey.Radio)))];

        ByHour = HourBars(summary.ByHour, out var peakHour);
        HourSummary = peakHour is { } hour ? $"Most often around {HourText(hour)}" : string.Empty;
        ByWeekday = WeekdayBars(summary.ByWeekday, out var peakDay);
        WeekdaySummary = peakDay is { } day ? $"Most of all on {CultureInfo.CurrentCulture.DateTimeFormat.GetDayName(day)}s" : string.Empty;
    }

    private static string Detail(int plays, TimeSpan listened) => $"{FormatPlays(plays)} · {FormatListened(listened)}";

    private static Track ToTrack(TopSong song) => new()
    {
        Title = song.Title,
        VideoId = song.VideoId,
        Artists = [.. song.Artists.Split(", ", StringSplitOptions.RemoveEmptyEntries).Select(name => new ArtistRef(name, null))],
        Thumbnails = song.ArtUrl is { } art ? [new Thumbnail(art, 0, 0)] : [],
    };

    private static string HourText(int hour) => DateTime.Today.AddHours(hour).ToString("t", CultureInfo.CurrentCulture);

    // Labels every six hours; the tooltip names each hour.
    private static List<ChartBar> HourBars(IReadOnlyList<TimeSpan> byHour, out int? peak)
    {
        var max = byHour.Count == 0 ? TimeSpan.Zero : byHour.Max();
        peak = max > TimeSpan.Zero ? byHour.ToList().IndexOf(max) : null;
        return
        [
            .. byHour.Select((time, hour) => new ChartBar(
                hour % 6 == 0 ? HourText(hour) : string.Empty,
                max > TimeSpan.Zero ? time / max : 0,
                $"{HourText(hour)}: {FormatListened(time)}")),
        ];
    }

    // The core counts from Monday; the chart starts the week where the user's region does.
    private static List<ChartBar> WeekdayBars(IReadOnlyList<TimeSpan> byWeekday, out DayOfWeek? peak)
    {
        var format = CultureInfo.CurrentCulture.DateTimeFormat;
        var max = byWeekday.Count == 0 ? TimeSpan.Zero : byWeekday.Max();
        peak = null;
        List<ChartBar> bars = [];
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)format.FirstDayOfWeek + i) % 7);
            var time = byWeekday.Count == 7 ? byWeekday[((int)day + 6) % 7] : TimeSpan.Zero;
            if (max > TimeSpan.Zero && time == max && peak is null)
            {
                peak = day;
            }

            bars.Add(new ChartBar(format.GetAbbreviatedDayName(day), max > TimeSpan.Zero ? time / max : 0, $"{format.GetDayName(day)}: {FormatListened(time)}"));
        }

        return bars;
    }

    // Each logged play raises Changed; refresh at most every couple of seconds while the page is open.
    private void OnStatsChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        _ = RefreshLaterAsync();
    }

    private async Task RefreshLaterAsync()
    {
        try
        {
            await Task.Delay(RefreshDelay, NavigationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            Volatile.Write(ref _refreshQueued, 0);
        }

        Services.Dispatcher.Run(() =>
        {
            _ = LoadAsync();
            _ = LoadHighlightsAsync(force: false);
        });
    }
}
