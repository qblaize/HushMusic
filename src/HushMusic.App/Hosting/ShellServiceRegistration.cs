using Microsoft.Extensions.DependencyInjection;
using HushMusic.App.Services.Shell;
using HushMusic.App.Services.Updates;
using HushMusic.App.Services.Windowing.TaskbarWidget;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Hosting;

// Shell services: shell view models, player bar, queue/lyrics panel, search modal, sign-in, settings,
// image cache and the album-art accent.
internal static class ShellServiceRegistration
{
    public static IServiceCollection AddShellServices(this IServiceCollection services, PageRegistry pages)
    {
        services.AddHttpClient(ImageCache.HttpClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36");
        });
        services.AddSingleton<IImageCache, ImageCache>();
        services.AddSingleton<ISignInCoordinator, SignInCoordinator>();

        // Started by ShellViewModel.InitializeAsync once the window exists (it recolours XAML brushes).
        services.AddSingleton<IAccentColorService, AccentColorService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<IWindowModeService, WindowModeService>();
        services.AddSingleton<TaskbarPlayerService>();
        services.AddAppUpdates();

        services.AddSingleton<AccountViewModel>();
        services.AddSingleton<TrackBrowseIds>();
        services.AddSingleton<QueuePanelViewModel>();
        services.AddSingleton<LyricsViewModel>();
        services.AddSingleton<RelatedViewModel>();
        services.AddSingleton<SleepTimerViewModel>();
        services.AddSingleton<CoverFlowViewModel>();
        services.AddSingleton<PlayerViewModel>();
        services.AddSingleton<NowPlayingViewModel>();
        services.AddSingleton<SearchBoxViewModel>();
        services.AddSingleton<ShellViewModel>();

        services.AddTransient<SettingsViewModel>();
        pages.Register<SettingsPage>(PageKey.Settings);
        return services;
    }
}
