using Microsoft.Extensions.DependencyInjection;

namespace HushMusic.App.Services.Updates;

internal static class UpdateServiceRegistration
{
    public static IServiceCollection AddAppUpdates(this IServiceCollection services)
    {
        services.AddSingleton<VelopackUpdateService>();
        services.AddSingleton<IUpdateService>(sp => sp.GetRequiredService<VelopackUpdateService>());
        services.AddHostedService(sp => sp.GetRequiredService<VelopackUpdateService>());
        return services;
    }
}
