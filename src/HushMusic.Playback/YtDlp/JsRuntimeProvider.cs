using System.ComponentModel;
using Microsoft.Extensions.Logging;

namespace HushMusic.Playback.YtDlp;

/// <summary>
/// Picks yt-dlp's JavaScript runtime for each run (see <see cref="JsRuntimeSelector"/>) and manages the app's own Deno:
/// installs it on first need, checks for updates weekly or after yt-dlp reports a runtime problem, and removes old versions.
/// </summary>
internal sealed class JsRuntimeProvider
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan BackgroundInstallRetry = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedInstallRetry = TimeSpan.FromMinutes(1);

    private readonly string _root;
    private readonly string _denoCache;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly Lazy<Task<string?>> _nodeOnPath;
    private readonly Lazy<Task<string?>> _denoOnPath;
    private volatile InstalledDeno? _managed;
    private long _lastBackgroundInstallTicks;
    private long _lastFailedInstallTicks;
    private int _problemCheckStarted;

    public JsRuntimeProvider(string toolsFolder, string cacheFolder, ILogger logger)
    {
        _root = Path.Combine(toolsFolder, "deno");
        _denoCache = Path.Combine(cacheFolder, "deno");
        _logger = logger;
        _nodeOnPath = new(() => Task.Run(() => ProbeAsync("node.exe", JsRuntimeSelector.MinimumNodeVersion)));
        _denoOnPath = new(() => Task.Run(() => ProbeAsync("deno.exe", DenoInstaller.MinimumVersion)));
    }

    /// <summary>The newest installed managed Deno (null until it is installed).</summary>
    public InstalledDeno? ManagedDeno => _managed ??= DenoInstaller.FindInstalled(_root);

    /// <summary>
    /// A Deno download failed less than a minute ago: resolves don't retry yet, so a GitHub outage doesn't add a failed
    /// download to every track.
    /// </summary>
    public bool RecentlyFailed =>
        Interlocked.Read(ref _lastFailedInstallTicks) is var failed and not 0
        && Environment.TickCount64 - failed < (long)FailedInstallRetry.TotalMilliseconds;

    /// <summary>The runtime yt-dlp would use right now, without installing anything.</summary>
    public async Task<JsRuntimePlan> PlanAsync(string? setting, CancellationToken cancellationToken)
    {
        var node = JsRuntimeSelector.UsesNodeOnPath(setting)
            ? await _nodeOnPath.Value.WaitAsync(cancellationToken).ConfigureAwait(false)
            : null;
        var deno = JsRuntimeSelector.UsesDenoOnPath(setting)
            ? await _denoOnPath.Value.WaitAsync(cancellationToken).ConfigureAwait(false)
            : null;
        return JsRuntimeSelector.Choose(setting, ManagedDeno?.ExecutablePath, node, deno);
    }

    /// <summary>
    /// The runtime for a resolve. When the managed Deno is wanted but missing: with a Node.js fallback it is installed in
    /// the background; without one this waits for the install. An install failure is logged and the plan returned as is,
    /// so yt-dlp still runs (and reports what is missing).
    /// </summary>
    public async Task<JsRuntimePlan> GetForResolveAsync(string? setting, CancellationToken cancellationToken)
    {
        var plan = await PlanAsync(setting, cancellationToken).ConfigureAwait(false);
        if (!plan.WantsManagedDeno)
        {
            return plan;
        }

        if (plan.Source != JsRuntimeSource.None)
        {
            InstallInBackground();
            return plan;
        }

        if (RecentlyFailed)
        {
            _logger.LogDebug("The last Deno download failed moments ago; running yt-dlp without a JavaScript runtime");
            return plan;
        }

        try
        {
            await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Could not install Deno; running yt-dlp without a JavaScript runtime");
            return plan;
        }

        return await PlanAsync(setting, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Environment for a yt-dlp run (yt-dlp passes it on to Deno).</summary>
    public IReadOnlyDictionary<string, string>? GetEnvironment(JsRuntimePlan plan) => plan.Source == JsRuntimeSource.ManagedDeno
        ? new Dictionary<string, string>
        {
            // Deno keeps an analysis cache even with --no-code-cache: keep it in the app's cache, not %LOCALAPPDATA%\deno.
            ["DENO_DIR"] = _denoCache,
            ["DENO_NO_UPDATE_CHECK"] = "1",
        }
        : null;

    /// <summary>Downloads the managed Deno when it is missing. Returns true when this call installed it.</summary>
    public async Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        if (ManagedDeno is { } current && File.Exists(current.ExecutablePath))
        {
            return false;
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have installed it while we waited.
            _managed = DenoInstaller.FindInstalled(_root);
            if (_managed is not null)
            {
                return false;
            }

            _logger.LogInformation("Deno is missing; downloading the latest release");
            Version latest;
            string exe;
            try
            {
                latest = await DenoInstaller.GetLatestVersionAsync(cancellationToken).ConfigureAwait(false);
                exe = await DenoInstaller.InstallAsync(_root, latest, _logger, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                Interlocked.Exchange(ref _lastFailedInstallTicks, Environment.TickCount64);
                throw;
            }

            _managed = new InstalledDeno(latest, exe);
            Interlocked.Exchange(ref _lastFailedInstallTicks, 0);
            DenoInstaller.WriteLastCheck(_root, DateTimeOffset.UtcNow);
            DenoInstaller.RemoveOtherVersions(_root, latest, _logger);
            return true;
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>
    /// Installs the latest Deno into a new version folder when it is newer than the managed one. Without
    /// <paramref name="force"/> this only checks when the last check is a week old. Returns true when it updated.
    /// </summary>
    public async Task<bool> UpdateAsync(bool force, CancellationToken cancellationToken)
    {
        if (ManagedDeno is null || (!force && !DenoInstaller.IsUpdateCheckDue(DenoInstaller.ReadLastCheck(_root), DateTimeOffset.UtcNow)))
        {
            return false;
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installed = ManagedDeno;
            var latest = await DenoInstaller.GetLatestVersionAsync(cancellationToken).ConfigureAwait(false);
            DenoInstaller.WriteLastCheck(_root, DateTimeOffset.UtcNow);
            if (installed is not null && !DenoInstaller.IsNewer(latest, installed.Version))
            {
                _logger.LogInformation("Deno {Version} is up to date", installed.Version);
                DenoInstaller.RemoveOtherVersions(_root, installed.Version, _logger);
                return false;
            }

            var exe = await DenoInstaller.InstallAsync(_root, latest, _logger, cancellationToken).ConfigureAwait(false);
            _managed = new InstalledDeno(latest, exe);
            _logger.LogInformation("Deno updated from {Before} to {After}", installed?.Version.ToString() ?? "nothing", latest);
            DenoInstaller.RemoveOtherVersions(_root, latest, _logger);
            return true;
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>yt-dlp reported a JavaScript runtime problem: check the managed Deno for an update (once per session).</summary>
    public void OnRuntimeProblem(JsRuntimePlan plan)
    {
        if (plan.Source != JsRuntimeSource.ManagedDeno || Interlocked.Exchange(ref _problemCheckStarted, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateAsync(force: true, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Deno update check after a JavaScript runtime problem failed");
            }
        });
    }

    // At most once an hour, so playing with Node.js while offline does not retry the download on every track.
    private void InstallInBackground()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastBackgroundInstallTicks);
        if ((last != 0 && now - last < (long)BackgroundInstallRetry.TotalMilliseconds)
            || Interlocked.CompareExchange(ref _lastBackgroundInstallTicks, now, last) != last)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await EnsureInstalledAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not install Deno in the background; Node.js stays in use");
            }
        });
    }

    // A runtime on PATH that yt-dlp would accept (new enough), or null. Checked once per session.
    private async Task<string?> ProbeAsync(string fileName, Version minimum)
    {
        var path = JsRuntimeSelector.FindOnPath(fileName, Environment.GetEnvironmentVariable("PATH"), File.Exists);
        if (path is null)
        {
            _logger.LogDebug("No {Runtime} on PATH", fileName);
            return null;
        }

        try
        {
            var result = await ProcessRunner.RunAsync(path, ["--version"], ProbeTimeout, CancellationToken.None).ConfigureAwait(false);
            var version = JsRuntimeSelector.ParseVersionOutput(result.StandardOutput);
            if (result.ExitCode == 0 && version is not null && version >= minimum)
            {
                _logger.LogInformation("Found {Runtime} {Version} on PATH: {Path}", fileName, version, path);
                return path;
            }

            _logger.LogInformation(
                "Ignoring {Path}: version {Version} (exit {ExitCode}) is older than {Minimum}, the oldest yt-dlp supports",
                path,
                version?.ToString() ?? "unknown",
                result.ExitCode,
                minimum);
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or TimeoutException)
        {
            _logger.LogInformation(ex, "Ignoring {Path}: it could not be run", path);
            return null;
        }
    }
}
