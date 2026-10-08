using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Features.LastFm;
using HushMusic.Core.Queue;
using HushMusic.Core.Radio;
using HushMusic.Core.Services;

namespace HushMusic.Core;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Core services: settings, notifications, queue, playback actions, account actions.
    /// Features (IHostedService) that react to player/queue/account events are registered here too.
    /// </summary>
    public static IServiceCollection AddHushCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IAppPaths, AppPaths>();
        services.AddSingleton<ISettingsService, JsonSettingsService>();
        services.AddSingleton<INotificationService, NotificationService>();
        services.AddSingleton<IQueueService, QueueService>();
        services.AddSingleton<IPlaybackActions, PlaybackActions>();
        services.AddSingleton<IAccountActionsService, AccountActionsService>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<VolumeNormalizer>();
        services.AddSingleton<ISleepTimer, SleepTimer>();

        services.AddHostedService<PlayHistoryReporter>();
        services.AddHostedService<QueueAutoExtender>();
        services.AddHostedService<PlaybackSessionKeeper>();

        services.AddHttpClient(LastFmClient.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(20));
        services.AddSingleton<LastFmService>();
        services.AddSingleton<ILastFmService>(sp => sp.GetRequiredService<LastFmService>());
        services.AddHostedService(sp => sp.GetRequiredService<LastFmService>());

        // Live internet radio: the Radio Browser directory, favourites, ICY "now playing" titles, play counting.
        services.AddHttpClient(RadioBrowserClient.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
            client.DefaultRequestHeaders.UserAgent.Add(RadioBrowserClient.UserAgent);
        });
        services.AddHttpClient(RadioNowPlayingService.HttpClientName, client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan; // each read has its own timeout
            client.DefaultRequestHeaders.UserAgent.Add(RadioBrowserClient.UserAgent);
        }).ConfigurePrimaryHttpMessageHandler(RadioNowPlayingService.CreateHttpHandler);
        services.AddSingleton<IRadioDirectory, RadioBrowserClient>();
        services.AddSingleton<RadioFavoritesStore>();
        services.AddSingleton<IRadioFavorites>(sp => sp.GetRequiredService<RadioFavoritesStore>());
        services.AddHostedService(sp => sp.GetRequiredService<RadioFavoritesStore>());
        services.AddSingleton<RadioNowPlayingService>();
        services.AddSingleton<IRadioNowPlaying>(sp => sp.GetRequiredService<RadioNowPlayingService>());
        services.AddHostedService<RadioClickReporter>();
        return services;
    }
}
