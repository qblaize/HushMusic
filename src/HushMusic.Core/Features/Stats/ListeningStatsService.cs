using Microsoft.Extensions.DependencyInjection;

namespace HushMusic.Core.Features.Stats;

/// <summary>"Your stats": listening statistics kept only on this PC.</summary>
public interface IListeningStats
{
    /// <summary>Raised when a play was logged or the statistics were cleared (on a background thread).</summary>
    event EventHandler? Changed;

    Task<ListeningSummary> GetSummaryAsync(StatsPeriod period, CancellationToken cancellationToken = default);

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

    public Task ClearAsync(CancellationToken cancellationToken = default) => log.ClearAsync(cancellationToken);
}

public static class ListeningStatsServiceCollectionExtensions
{
    /// <summary>The local play log, the recorder that fills it and <see cref="IListeningStats"/>.</summary>
    public static IServiceCollection AddListeningStats(this IServiceCollection services)
    {
        services.AddSingleton<IPlayLog, JsonLinesPlayLog>();
        services.AddSingleton<IListeningStats, ListeningStatsService>();
        services.AddHostedService<ListeningRecorder>();
        return services;
    }
}
