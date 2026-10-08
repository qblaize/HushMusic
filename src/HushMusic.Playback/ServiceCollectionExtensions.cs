using Microsoft.Extensions.DependencyInjection;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Radio;

namespace HushMusic.Playback;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers stream resolution (live radio stations directly, everything else through yt-dlp, plus its startup
    /// install/update), and the MediaPlayer-based player with SMTC.
    /// </summary>
    public static IServiceCollection AddHushPlayback(this IServiceCollection services)
    {
        services.AddSingleton<YtDlpStreamResolver>();
        services.AddSingleton<IStreamResolver>(sp => new RadioStreamResolver(sp.GetRequiredService<YtDlpStreamResolver>()));
        services.AddSingleton<IPlayer, MediaPlayerService>();
        services.AddHostedService<YtDlpMaintenanceService>();
        return services;
    }
}
