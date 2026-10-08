using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Updates;

/// <summary>
/// <see cref="IUpdateService"/> over Velopack's <see cref="UpdateManager"/> with GitHub Releases as the feed
/// (configuration key <see cref="RepositoryKey"/>; empty turns update checks off).
/// </summary>
internal sealed class VelopackUpdateService : IUpdateService, IHostedService, IDisposable
{
    public const string RepositoryKey = "Updates:GitHubRepository";

    // Late enough not to compete with the first Home load and yt-dlp's own update check.
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(20);

    // The app often runs for days in the notification area.
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    private readonly UpdateManager? _manager;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowModeService _windowModes;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _sync = new();
    private VelopackAsset? _ready;
    private string? _notifiedVersion;
    private Task? _loop;

    public VelopackUpdateService(
        IConfiguration configuration,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        IWindowModeService windowModes,
        ILogger<VelopackUpdateService> logger)
    {
        _notifications = notifications;
        _dispatcher = dispatcher;
        _windowModes = windowModes;
        _logger = logger;

        var installed = InstalledVersion();
        CurrentVersion = installed ?? AssemblyVersion();
        var repository = configuration[RepositoryKey]?.Trim();
        if (installed is null)
        {
            State = UpdateState.NotInstalled;
        }
        else if (string.IsNullOrEmpty(repository))
        {
            State = UpdateState.Disabled;
        }
        else
        {
            try
            {
                _manager = new UpdateManager(new GithubSource(repository, accessToken: null, prerelease: false));
                State = UpdateState.Idle;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Updates: invalid update source {Repository}", repository);
                State = UpdateState.Failed;
                ErrorMessage = "The update source isn't a valid GitHub repository.";
            }
        }
    }

    public event EventHandler? StateChanged;

    public string CurrentVersion { get; }

    public UpdateState State { get; private set; }

    public bool CanCheck => _manager is not null;

    public string? AvailableVersion { get; private set; }

    public int DownloadProgress { get; private set; }

    public string? ErrorMessage { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Updates: version {Version}, {State}", CurrentVersion, State);
        if (_manager is not null)
        {
            _loop = Task.Run(() => RunAsync(_manager, _stopping.Token), CancellationToken.None);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        if (_loop is not null)
        {
            await Task.WhenAny(_loop, Task.Delay(Timeout.Infinite, cancellationToken)).ConfigureAwait(false);
        }
    }

    public void Dispose() => _stopping.Dispose();

    public async Task<UpdateState> CheckAsync(CancellationToken cancellationToken = default)
    {
        // One check at a time, and nothing more to do once an update waits for a restart.
        if (_manager is null || State == UpdateState.ReadyToRestart || !_checking.Wait(0))
        {
            return State;
        }

        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            var ct = linked.Token;
            lock (_sync)
            {
                AvailableVersion = null;
                DownloadProgress = 0;
            }

            Update(UpdateState.Checking, error: null);
            var update = await _manager.CheckForUpdatesAsync().WaitAsync(ct).ConfigureAwait(false);
            if (update is null)
            {
                _logger.LogInformation("Updates: {Version} is the latest version", CurrentVersion);
                Update(UpdateState.UpToDate, error: null);
                return State;
            }

            var target = update.TargetFullRelease;
            _logger.LogInformation("Updates: downloading {Version}", target.Version);
            Update(UpdateState.Downloading, error: null, available: target.Version.ToString(), progress: 0);
            await _manager.DownloadUpdatesAsync(update, ReportProgress, ct).ConfigureAwait(false);
            MarkReady(target);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            Update(UpdateState.Idle, error: null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updates: check or download failed");
            Update(UpdateState.Failed, error: Describe(ex));
        }
        finally
        {
            _checking.Release();
        }

        return State;
    }

    public void RestartToUpdate()
    {
        var ready = _ready;
        if (_manager is null || ready is null)
        {
            return;
        }

        try
        {
            // Starts the updater, which waits (up to 60 s) for this process to exit, installs, then starts the new version.
            _manager.WaitExitThenApplyUpdates(ready, silent: false, restart: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Updates: could not start the updater");
            _notifications.ShowError("Couldn't install the update", ex);
            return;
        }

        _logger.LogInformation("Updates: quitting to install {Version}", ready.Version);

        // The tray's Quit: the normal shutdown that saves the queue and stops the features. Queued, so the button that
        // asked for it finishes its click before the window closes.
        _ = Task.Run(() => _dispatcher.Run(() => (_windowModes as WindowModeService)?.Quit()));
    }

    // The version of this install, or null when the app wasn't installed by Velopack (development builds).
    private static string? InstalledVersion()
    {
        try
        {
            return VelopackLocator.IsCurrentSet ? VelopackLocator.Current.CurrentlyInstalledVersion?.ToString() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // <Version> from Directory.Build.props, without the "+commit" suffix the SDK may add.
    private static string AssemblyVersion()
    {
        var version = typeof(VelopackUpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(VelopackUpdateService).Assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
        var metadata = version.IndexOf('+', StringComparison.Ordinal);
        return metadata >= 0 ? version[..metadata] : version;
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException => "Couldn't reach GitHub. Check your connection and try again.",
        TimeoutException or TaskCanceledException => "GitHub took too long to answer. Try again later.",
        _ => ex.Message,
    };

    private async Task RunAsync(UpdateManager manager, CancellationToken ct)
    {
        try
        {
            // Downloaded in an earlier session but not installed: the app never installs on launch (a second launch
            // would have to stop the running copy), so it asks again.
            if (manager.UpdatePendingRestart is { } pending)
            {
                MarkReady(pending);
            }

            await Task.Delay(FirstCheckDelay, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(CheckInterval);
            do
            {
                await CheckAsync(ct).ConfigureAwait(false);
            }
            while (State != UpdateState.ReadyToRestart && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updates: background checks stopped");
        }
    }

    private void MarkReady(VelopackAsset asset)
    {
        var version = asset.Version.ToString();
        _ready = asset;
        Update(UpdateState.ReadyToRestart, error: null, available: version, progress: 100);
        _logger.LogInformation("Updates: {Version} is ready to install", version);

        if (_notifiedVersion != version)
        {
            _notifiedVersion = version;
            _notifications.Show(new AppNotification(NotificationSeverity.Success, "Update ready — restart to install", $"Hush {version} has been downloaded.")
            {
                Action = new NotificationAction("Restart", RestartToUpdate),
            });
        }
    }

    private void ReportProgress(int percent)
    {
        lock (_sync)
        {
            if (State != UpdateState.Downloading || percent == DownloadProgress)
            {
                return;
            }

            DownloadProgress = percent;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Update(UpdateState state, string? error, string? available = null, int? progress = null)
    {
        lock (_sync)
        {
            State = state;
            ErrorMessage = error;
            AvailableVersion = available ?? AvailableVersion;
            DownloadProgress = progress ?? DownloadProgress;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
