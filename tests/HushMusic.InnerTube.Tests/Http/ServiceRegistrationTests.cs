using Microsoft.Extensions.DependencyInjection;
using HushMusic.Core.Abstractions;
using HushMusic.InnerTube.Http;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

public sealed class ServiceRegistrationTests
{
    [Fact]
    public void AddHushInnerTube_resolves_every_api_as_a_singleton()
    {
        using var paths = new TempAppPaths();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAppPaths>(paths)
            .AddSingleton<ISettingsService, FakeSettingsService>()
            .AddSingleton<IRequestAuthenticator, FakeAuthenticator>()
            .AddHushInnerTube();

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.NotNull(provider.GetRequiredService<IBrowseApi>());
        Assert.NotNull(provider.GetRequiredService<ISearchApi>());
        Assert.NotNull(provider.GetRequiredService<ILibraryApi>());
        Assert.NotNull(provider.GetRequiredService<IWatchApi>());
        Assert.NotNull(provider.GetRequiredService<IAccountApi>());
        Assert.Same(provider.GetRequiredService<IInnerTubeClient>(), provider.GetRequiredService<IInnerTubeClient>());

        var http = provider.GetRequiredService<IHttpClientFactory>().CreateClient(InnerTubeClient.HttpClientName);
        Assert.Equal(TimeSpan.FromSeconds(30), http.Timeout);
    }
}
