using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using HushMusic.Core.Abstractions;
using HushMusic.InnerTube.Api;
using HushMusic.InnerTube.Http;

namespace HushMusic.InnerTube;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the InnerTube implementations of the Core music API interfaces.
    /// Requires <see cref="IRequestAuthenticator"/>, <see cref="ISettingsService"/> and <see cref="IAppPaths"/>.
    /// Optional: configure <see cref="InnerTubeOptions"/>.
    /// </summary>
    public static IServiceCollection AddHushInnerTube(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<InnerTubeOptions>();

        services.AddHttpClient(InnerTubeClient.HttpClientName)
            .ConfigureHttpClient((sp, http) => http.Timeout = sp.GetRequiredService<IOptions<InnerTubeOptions>>().Value.Timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                // The Cookie header is set explicitly per request (account cookies or the SOCS consent cookie).
                UseCookies = false,
                AutomaticDecompression = DecompressionMethods.All,
            })
            .RedactLoggedHeaders(_ => true);

        services.AddSingleton<VisitorIdProvider>();
        services.AddSingleton<IInnerTubeClient, InnerTubeClient>();

        services.AddSingleton<IBrowseApi, BrowseApi>();
        services.AddSingleton<ISearchApi, SearchApi>();
        services.AddSingleton<ILibraryApi, LibraryApi>();
        services.AddSingleton<IWatchApi, WatchApi>();
        services.AddSingleton<IAccountApi, AccountApi>();
        return services;
    }
}
