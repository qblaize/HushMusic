using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Abstractions;

namespace HushMusic.Playback;

/// <summary>What still has to be downloaded before playback works.</summary>
/// <param name="YtDlp">The managed yt-dlp is missing.</param>
/// <param name="Deno">The managed Deno is the chosen JavaScript runtime but is missing.</param>
/// <param name="HasNodeFallback">Node.js on PATH can stand in for Deno until it is installed.</param>
internal sealed record MissingComponents(bool YtDlp, bool Deno, bool HasNodeFallback)
{
    /// <summary>Nothing can play until the downloads finish.</summary>
    public bool BlocksPlayback => YtDlp || (Deno && !HasNodeFallback);
}

/// <summary>
/// At startup, in the background: downloads yt-dlp and Deno (its JavaScript runtime) side by side when they are missing,
/// otherwise updates them (yt-dlp on every start, as YouTube regularly breaks old versions; Deno weekly). Never delays
/// app startup; a resolve that needs a missing component waits for it.
/// </summary>
public sealed class YtDlpMaintenanceService(
    YtDlpStreamResolver resolver,
    ISettingsService settings,
    INotificationService notifications,
    ILogger<YtDlpMaintenanceService> logger) : IHostedService, IDisposable
{
    // Download sizes of the x64 builds (2026-10): yt-dlp_win.zip ~18 MB, deno-x86_64-pc-windows-msvc.zip ~43 MB.
    private const int YtDlpDownloadMegabytes = 20;
    private const int DenoDownloadMegabytes = 45;

    private readonly CancellationTokenSource _stopping = new();
    private Task? _work;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = resolver.EnsureCacheLoadedAsync();
        _work = Task.Run(() => RunAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        if (_work is not null)
        {
            await _work.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    public void Dispose() => _stopping.Dispose();

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!resolver.UsesManagedExecutable)
            {
                CheckUserSuppliedYtDlp();
            }

            var missing = await resolver.GetMissingComponentsAsync(cancellationToken).ConfigureAwait(false);
            if (missing.YtDlp || missing.Deno)
            {
                await SetUpAsync(missing, cancellationToken).ConfigureAwait(false);
                return;
            }

            if (settings.Current.CheckYtDlpUpdatesOnStartup)
            {
                await Task.WhenAll(UpdateYtDlpAsync(cancellationToken), UpdateDenoAsync(cancellationToken)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Playback component maintenance failed");
        }
    }

    // First run (or after a failed one): both downloads in parallel. Only a setup that playback has to wait for is
    // announced; Deno installing behind a working Node.js is just logged.
    private async Task SetUpAsync(MissingComponents missing, CancellationToken cancellationToken)
    {
        var announce = missing.BlocksPlayback;
        if (announce)
        {
            var megabytes = (missing.YtDlp ? YtDlpDownloadMegabytes : 0) + (missing.Deno ? DenoDownloadMegabytes : 0);
            notifications.ShowInfo(
                "Setting up playback",
                $"Downloading the components needed to play music (about {megabytes} MB). This only happens once.");
        }

        var ytDlp = missing.YtDlp ? resolver.EnsureInstalledAsync(cancellationToken) : Task.FromResult(false);
        var deno = missing.Deno ? resolver.EnsureJsRuntimeInstalledAsync(cancellationToken) : Task.FromResult(false);
        var ytDlpError = await CaptureAsync(ytDlp, "yt-dlp", cancellationToken).ConfigureAwait(false);
        var denoError = await CaptureAsync(deno, "Deno", cancellationToken).ConfigureAwait(false);

        if (ytDlpError is null && denoError is null)
        {
            if (announce)
            {
                var installed = new List<string>();
                if (missing.YtDlp)
                {
                    installed.Add($"yt-dlp {await resolver.GetBackendVersionAsync(cancellationToken).ConfigureAwait(false)}");
                }

                if (missing.Deno)
                {
                    installed.Add($"Deno {resolver.ManagedDenoVersion}");
                }

                notifications.Show(new AppNotification(NotificationSeverity.Success, "Playback is ready", $"Installed {string.Join(" and ", installed)}."));
            }

            return;
        }

        if (ytDlpError is null && missing.HasNodeFallback)
        {
            if (announce)
            {
                notifications.Show(new AppNotification(
                    NotificationSeverity.Warning,
                    "Couldn't download Deno",
                    $"{Describe(denoError!, "Deno")} Playback uses Node.js until it can be downloaded.",
                    denoError));
            }

            return;
        }

        var error = ytDlpError ?? denoError!;
        notifications.Show(new AppNotification(
            NotificationSeverity.Error,
            "Couldn't set up playback",
            $"{Describe(error, ytDlpError is not null ? "yt-dlp" : "Deno")} The app tries again when you play something.",
            error));
    }

    private void CheckUserSuppliedYtDlp()
    {
        if (!File.Exists(resolver.ExecutablePath))
        {
            notifications.Show(new AppNotification(
                NotificationSeverity.Warning,
                "yt-dlp is not available",
                $"yt-dlp was not found at \"{resolver.ExecutablePath}\". Fix the path in Settings, or clear it to let the app manage its own copy."));
        }
    }

    private async Task<Exception?> CaptureAsync(Task task, string component, CancellationToken cancellationToken)
    {
        try
        {
            await task.ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "{Component} install failed", component);
            return ex;
        }
    }

    private async Task UpdateYtDlpAsync(CancellationToken cancellationToken)
    {
        if (!resolver.UsesManagedExecutable)
        {
            return;
        }

        try
        {
            if (await resolver.UpdateYtDlpAsync(cancellationToken).ConfigureAwait(false))
            {
                var version = await resolver.GetBackendVersionAsync(cancellationToken).ConfigureAwait(false);
                notifications.ShowInfo("yt-dlp updated", $"yt-dlp was updated to {version}.");
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "yt-dlp update failed");
            notifications.Show(new AppNotification(
                NotificationSeverity.Warning,
                "Couldn't update yt-dlp",
                $"{ex.Message} Playback may fail if the installed version is too old.",
                ex));
        }
    }

    // Weekly and silent: the installed Deno keeps working, so neither an update nor a failure needs the user.
    private async Task UpdateDenoAsync(CancellationToken cancellationToken)
    {
        try
        {
            await resolver.UpdateJsRuntimeAsync(force: false, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Deno update failed; keeping the installed version");
        }
    }

    private static string Describe(Exception error, string component) => error switch
    {
        HttpRequestException or TaskCanceledException => $"Couldn't download {component}. Check your internet connection.",
        HushException => error.Message,
        _ => $"Couldn't install {component}: {error.Message}",
    };
}
