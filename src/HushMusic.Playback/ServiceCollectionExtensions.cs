using Microsoft.Extensions.DependencyInjection;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Radio;

namespace HushMusic.Playback;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers stream resolution (live radio stations directly, everything else through yt-dlp, plus its startup
    /// install/update), the MediaPlayer-based player with SMTC, and the music-video picture beside it.
    /// </summary>
    public static IServiceCollection AddHushPlayback(this IServiceCollection services)
    {
        services.AddSingleton<YtDlpStreamResolver>();
        services.AddSingleton<IStreamResolver>(sp => new RadioStreamResolver(sp.GetRequiredService<YtDlpStreamResolver>()));
        services.AddSingleton<IPlayer, MediaPlayerService>();

        // Music videos come from YouTube only (radio stations have no picture), so straight from yt-dlp.
        services.AddSingleton(sp => ActivatorUtilities.CreateInstance<MusicVideoPlayer>(sp, sp.GetRequiredService<YtDlpStreamResolver>()));
        services.AddHostedService<YtDlpMaintenanceService>();
        return services;
    }
}
