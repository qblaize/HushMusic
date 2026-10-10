using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using HushMusic.App.Services.Notifications;
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

    // Often, so a release reaches a copy that runs for days in the notification area within minutes. One small GitHub
    // request each time (the anonymous limit is 60 an hour).
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly UpdateManager? _manager;
    private readonly INotificationService _notifications;
    private readonly TrackNotificationService _windowsNotifications;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly IWindowModeService _windowModes;
    private readonly ILogger<VelopackUpdateService> _logger;
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly Lock _sync = new();
    private UpdateInfo? _available;
    private VelopackAsset? _ready;
    private string? _offeredVersion;
    private string? _readyNoticeVersion;
    private bool _loggedUpToDate;
    private Task? _loop;

    public VelopackUpdateService(
        IConfiguration configuration,
        INotificationService notifications,
        TrackNotificationService windowsNotifications,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        IWindowModeService windowModes,
        ILogger<VelopackUpdateService> logger)
    {
        _notifications = notifications;
        _windowsNotifications = windowsNotifications;
        _settings = settings;
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

    public Task<UpdateState> CheckAsync(CancellationToken cancellationToken = default) =>
        CheckAsync(background: false, cancellationToken);

    public async Task<UpdateState> DownloadAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null)
        {
            return State;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
        var ct = linked.Token;

        // Waits for a check that is running right now (the button stays on screen during a background check).
        await _busy.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != UpdateState.Available || _available is not { } update)
            {
                return State;
            }

            _windowsNotifications.RemoveUpdateNotification();
            var target = update.TargetFullRelease;
            _logger.LogInformation("Updates: downloading {Version}", target.Version);
            Update(UpdateState.Downloading, error: null, progress: 0);
            await _manager.DownloadUpdatesAsync(update, ReportProgress, ct).ConfigureAwait(false);
            MarkReady(target, windowsNotification: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Update(UpdateState.Available, error: null);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updates: download failed");
            Update(UpdateState.Failed, error: Describe(ex));
        }
        finally
        {
            _busy.Release();
        }

        return State;
    }

    public void SkipAvailableVersion()
    {
        var version = AvailableVersion;
        if (State != UpdateState.Available || version is null)
        {
            return;
        }

        _logger.LogInformation("Updates: skipping {Version}", version);
        _windowsNotifications.RemoveUpdateNotification();
        _ = SaveSkippedAsync(version);
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
                MarkReady(pending, windowsNotification: false);
            }

            await Task.Delay(FirstCheckDelay, ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(CheckInterval);
            do
            {
                await CheckAsync(background: true, ct).ConfigureAwait(false);
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

    // Finds out whether a newer version is out; never downloads it (DownloadAsync does, when asked).
    private async Task<UpdateState> CheckAsync(bool background, CancellationToken cancellationToken)
    {
        // One check or download at a time, and nothing to look for while one waits for a restart.
        if (_manager is null || State is UpdateState.ReadyToRestart or UpdateState.Downloading || !_busy.Wait(0))
        {
            return State;
        }

        // A background check while an update is on offer keeps the offer on screen; it only changes when a newer
        // version comes out (or none is left).
        var quiet = background && State == UpdateState.Available;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopping.Token);
            if (!quiet)
            {
                Update(UpdateState.Checking, error: null);
            }

            var update = await _manager.CheckForUpdatesAsync().WaitAsync(linked.Token).ConfigureAwait(false);
            if (update is null)
            {
                // Every five minutes: only the first answer goes to the log at Information.
                _logger.Log(
                    _loggedUpToDate ? LogLevel.Debug : LogLevel.Information,
                    "Updates: {Version} is the latest version",
                    CurrentVersion);
                _loggedUpToDate = true;
                _available = null;
                Update(UpdateState.UpToDate, error: null, available: string.Empty);
                return State;
            }

            Offer(update, notify: background);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested || _stopping.IsCancellationRequested)
        {
            if (!quiet)
            {
                Update(UpdateState.Idle, error: null);
            }

            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updates: check failed");
            if (!quiet)
            {
                Update(UpdateState.Failed, error: Describe(ex), available: string.Empty);
            }
        }
        finally
        {
            _busy.Release();
        }

        return State;
    }

    // A newer version is out: Settings offers it, and the first time this session (unless it was skipped) an in-app
    // notice and a Windows notification ask whether to download it.
    private void Offer(UpdateInfo update, bool notify)
    {
        var version = update.TargetFullRelease.Version.ToString();
        _available = update;
        var isNew = _offeredVersion != version;
        _offeredVersion = version;
        Update(UpdateState.Available, error: null, available: version);
        if (!isNew)
        {
            return;
        }

        var skipped = string.Equals(_settings.Current.SkippedUpdateVersion, version, StringComparison.OrdinalIgnoreCase);
        _logger.LogInformation("Updates: {Version} is available{Skipped}", version, skipped ? " (skipped)" : string.Empty);
        if (!notify || skipped)
        {
            return;
        }

        _notifications.Show(new AppNotification(NotificationSeverity.Informational, "New update available", $"Hush {version} is out. Download it now, or skip this version.")
        {
            Action = new NotificationAction("Download", StartDownload),
            SecondaryAction = new NotificationAction("Skip", SkipAvailableVersion),
        });
        _windowsNotifications.ShowUpdateAvailable(version, StartDownload, SkipAvailableVersion);
    }

    // From a notification button: errors are shown by the state (Settings) and a notice.
    private void StartDownload() => _ = Task.Run(async () =>
    {
        try
        {
            if (await DownloadAsync().ConfigureAwait(false) == UpdateState.Failed)
            {
                _notifications.Show(new AppNotification(NotificationSeverity.Error, "Couldn't download the update", ErrorMessage ?? string.Empty));
            }
        }
        catch (OperationCanceledException)
        {
            // The app is shutting down.
        }
    });

    private async Task SaveSkippedAsync(string version)
    {
        try
        {
            await _settings.UpdateAsync(s => s.SkippedUpdateVersion = version).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Updates: could not save the skipped version");
        }
    }

    // The in-app notice, plus a Windows notification (seen while Hush is minimized or in the notification area) when
    // the download has just finished. Once per version.
    private void MarkReady(VelopackAsset asset, bool windowsNotification)
    {
        var version = asset.Version.ToString();
        _ready = asset;
        Update(UpdateState.ReadyToRestart, error: null, available: version, progress: 100);
        _logger.LogInformation("Updates: {Version} is ready to install", version);

        if (_readyNoticeVersion != version)
        {
            _readyNoticeVersion = version;
            _notifications.Show(new AppNotification(NotificationSeverity.Success, "Update ready — restart to install", $"Hush {version} has been downloaded.")
            {
                Action = new NotificationAction("Restart", RestartToUpdate),
            });
            if (windowsNotification)
            {
                _windowsNotifications.ShowUpdateReady(version, RestartToUpdate);
            }
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

    // available: null keeps the current version, empty clears it.
    private void Update(UpdateState state, string? error, string? available = null, int? progress = null)
    {
        lock (_sync)
        {
            State = state;
            ErrorMessage = error;
            AvailableVersion = available is null ? AvailableVersion : available.Length == 0 ? null : available;
            DownloadProgress = progress ?? DownloadProgress;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
