using Microsoft.Extensions.DependencyInjection;

namespace HushMusic.Core.Features.Stats;

/// <summary>The all-time extras of "Your stats": the years there is something to look back on, and the badges.</summary>
public sealed record StatsHighlights(IReadOnlyList<ListeningYear> Years, AchievementReport Achievements);

/// <summary>"Your stats": listening statistics kept only on this PC.</summary>
public interface IListeningStats
{
    /// <summary>Raised when a play was logged or the statistics were cleared (on a background thread).</summary>
    event EventHandler? Changed;

    Task<ListeningSummary> GetSummaryAsync(StatsPeriod period, CancellationToken cancellationToken = default);

    /// <summary>The years with listening (newest first) and the achievements, from one read of the whole log.</summary>
    Task<StatsHighlights> GetHighlightsAsync(CancellationToken cancellationToken = default);

    /// <summary>"Your year in Hush" for a calendar year (the current one so far).</summary>
    Task<YearReview> GetYearReviewAsync(int year, CancellationToken cancellationToken = default);

    /// <summary>Deletes every logged play.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

internal sealed class ListeningStatsService(IPlayLog log, TimeProvider time) : IListeningStats
{
    public event EventHandler? Changed
    {
        add => log.Changed += value;
        remove => log.Changed -= value;
    }

    public async Task<ListeningSummary> GetSummaryAsync(StatsPeriod period, CancellationToken cancellationToken = default)
    {
        var now = time.GetLocalNow();
        var records = await log.ReadAsync(ListeningStatsCalculator.PeriodStart(period, now), cancellationToken).ConfigureAwait(false);
        return ListeningStatsCalculator.Compute(records, period, now, time.LocalTimeZone);
    }

    public async Task<StatsHighlights> GetHighlightsAsync(CancellationToken cancellationToken = default)
    {
        var now = time.GetLocalNow();

        // Like the summaries, nothing after now counts (a clock that was set wrong once).
        var records = (await log.ReadAsync(null, cancellationToken).ConfigureAwait(false)).Where(r => r.Start <= now).ToList();
        return new StatsHighlights(
            YearReviewCalculator.Years(records, time.LocalTimeZone),
            Achievements.Compute(records, DateOnly.FromDateTime(now.DateTime), time.LocalTimeZone));
    }

    public async Task<YearReview> GetYearReviewAsync(int year, CancellationToken cancellationToken = default)
    {
        // A day early: the files are by the year in the offset of each play, which can differ from today's.
        var since = YearReviewCalculator.YearStart(year, time.LocalTimeZone).AddDays(-1);
        var records = await log.ReadAsync(since, cancellationToken).ConfigureAwait(false);
        return YearReviewCalculator.Compute(records, year, time.GetLocalNow(), time.LocalTimeZone);
    }

    public Task ClearAsync(CancellationToken cancellationToken = default) => log.ClearAsync(cancellationToken);
}

public static class ListeningStatsServiceCollectionExtensions
{
    /// <summary>The local play log, the recorder that fills it, <see cref="IListeningStats"/> and the achievement notices.</summary>
    public static IServiceCollection AddListeningStats(this IServiceCollection services)
    {
        services.AddSingleton<IPlayLog, JsonLinesPlayLog>();
        services.AddSingleton<IListeningStats, ListeningStatsService>();
        services.AddHostedService<ListeningRecorder>();
        services.AddHostedService<AchievementWatcher>();
        return services;
    }
}
