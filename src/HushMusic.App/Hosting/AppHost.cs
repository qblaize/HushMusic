using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using HushMusic.Auth;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Services;
using HushMusic.InnerTube;
using HushMusic.Playback;

namespace HushMusic.App.Hosting;

internal static class AppHost
{
    public static IHost Build(DispatcherQueue dispatcherQueue)
    {
        var paths = new AppPaths();
        // No defaults: they add configuration sources the app doesn't read (command line, every environment variable),
        // logging providers it replaces with Serilog, and a file watcher that reloads appsettings.json, which only an
        // update changes.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "HushMusic",
            ContentRootPath = AppContext.BaseDirectory,
            DisableDefaults = true,
        });

        // appsettings.json (the update source), with optional overrides from HUSHMUSIC_ environment variables.
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        builder.Configuration.AddEnvironmentVariables("HUSHMUSIC_");

        var levelSwitch = new LoggingLevelSwitch(LogEventLevel.Information);
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(levelSwitch);
        builder.Services.AddSerilog(
            (_, config) => config
                .MinimumLevel.ControlledBy(levelSwitch)
                .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .WriteTo.File(
                    Path.Combine(paths.Logs, "hushmusic-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    fileSizeLimitBytes: 10 * 1024 * 1024,
                    rollOnFileSizeLimit: true,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}"),
            preserveStaticLogger: false,
            writeToProviders: false);

        builder.Services.AddSingleton<IAppPaths>(paths);
        builder.Services
            .AddHushCore()
            .AddHushInnerTube()
            .AddHushAuth()
            .AddHushPlayback();

        var pages = new PageRegistry();
        builder.Services.AddSingleton(pages);
        builder.Services.AddSingleton<IUiDispatcher>(new UiDispatcher(dispatcherQueue));
        builder.Services.AddSingleton<INavigationService, NavigationService>();
        builder.Services.AddSingleton<IDialogService, DialogService>();
        builder.Services.AddShellServices(pages);
        builder.Services.AddPageServices(pages);

        return builder.Build();
    }

    public static void ApplyLogLevel(IServiceProvider services, string level)
    {
        if (Enum.TryParse<LogEventLevel>(level, ignoreCase: true, out var parsed))
        {
            services.GetRequiredService<LoggingLevelSwitch>().MinimumLevel = parsed;
        }
    }
}
