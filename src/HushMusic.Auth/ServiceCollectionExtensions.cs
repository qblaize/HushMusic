using Microsoft.Extensions.DependencyInjection;
using HushMusic.Auth.Storage;
using HushMusic.Core.Abstractions;

namespace HushMusic.Auth;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers cookie sign-in, encrypted credential storage and the InnerTube request authenticator.</summary>
    public static IServiceCollection AddHushAuth(this IServiceCollection services)
    {
        services.AddSingleton<AuthService>();
        services.AddSingleton<IAuthService>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<IRequestAuthenticator>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<ICookieFileProvider>(sp => sp.GetRequiredService<AuthService>());
        services.AddSingleton<ISecretStore, DpapiSecretStore>();
        return services;
    }
}
