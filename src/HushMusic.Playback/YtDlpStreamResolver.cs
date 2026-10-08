using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Playback.YtDlp;

namespace HushMusic.Playback;

/// <summary>
/// Resolves audio stream URLs by running yt-dlp.exe. Results are cached until shortly before they expire,
/// and concurrent requests for the same track share one yt-dlp process. yt-dlp's JavaScript runtime (normally the
/// app's own Deno) comes from <see cref="JsRuntimeProvider"/>.
/// </summary>
public sealed class YtDlpStreamResolver(
    ISettingsService settings,
    IAppPaths paths,
    IAuthService auth,
    ICookieFileProvider cookieFiles,
    INotificationService notifications,
    ILogger<YtDlpStreamResolver> logger) : IStreamResolver
{
    private const int MaxCacheEntries = 256;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);

    // Bump when the yt-dlp format selection changes, so links resolved with the old one are not reused.
    private const string PersistedCacheVersion = "webm-1";

    private readonly ConcurrentDictionary<string, ResolvedStream> _cache = new(StringComparer.Ordinal);
    private Task? _cacheLoad;
    private int _savePending;
    private readonly Dictionary<string, Resolution> _inFlight = new(StringComparer.Ordinal);
    private readonly object _inFlightGate = new();
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private readonly JsRuntimeProvider _jsRuntimes = new(paths.Tools, paths.Cache, logger);
    private int _setupNoticeShown;

    // Newest installed app-managed build; reset whenever an install or update changes it.
    private volatile InstalledYtDlp? _managed;

    /// <summary>False when the user pointed <see cref="AppSettings.YtDlpPath"/> at their own yt-dlp; that copy is never updated.</summary>
    public bool UsesManagedExecutable => string.IsNullOrWhiteSpace(settings.Current.YtDlpPath);

    /// <summary>The yt-dlp.exe that runs: the user's own, or the newest app-managed build (null until it is installed).</summary>
    public string? ExecutablePath => UsesManagedExecutable ? ManagedInstall?.ExecutablePath : settings.Current.YtDlpPath!.Trim();

    /// <summary>App-managed builds live in tools\yt-dlp\&lt;version&gt;\.</summary>
    private string ManagedRoot => Path.Combine(paths.Tools, "yt-dlp");

    private InstalledYtDlp? ManagedInstall => _managed ??= YtDlpInstaller.FindInstalled(ManagedRoot);

    private string CacheFile => Path.Combine(paths.Cache, "streams.json");

    public Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default) =>
        ResolveAsync(videoId, interactive: true, cancellationToken);

    private async Task<ResolvedStream> ResolveAsync(string videoId, bool interactive, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        await EnsureCacheLoadedAsync().ConfigureAwait(false);
        if (TryGetCached(videoId, out var cached))
        {
            return cached;
        }

        if (interactive)
        {
            await NotifyIfStillSettingUpAsync(cancellationToken).ConfigureAwait(false);
        }

        Resolution resolution;
        lock (_inFlightGate)
        {
            if (!_inFlight.TryGetValue(videoId, out resolution!) || resolution.Cts.IsCancellationRequested)
            {
                resolution = StartResolution(videoId);
                _inFlight[videoId] = resolution;
            }

            resolution.Waiters++;
        }

        var abandon = false;
        try
        {
            // ForceYielding: a caller cancelling while holding its own lock must not run our cleanup inline.
            return await resolution.Task.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        }
        finally
        {
            lock (_inFlightGate)
            {
                abandon = --resolution.Waiters == 0 && !resolution.Task.IsCompleted;
            }

            // Everyone gave up (e.g. the user skipped ahead): stop yt-dlp instead of letting it run to the timeout.
            if (abandon)
            {
                resolution.Cts.Cancel();
            }
        }
    }

    public void Invalidate(string videoId)
    {
        if (_cache.TryRemove(videoId, out _))
        {
            ScheduleSave();
        }
    }

    /// <summary>Loads links resolved in earlier sessions (they stay valid for ~6 h). Safe to call repeatedly.</summary>
    public Task EnsureCacheLoadedAsync() => LazyInitializer.EnsureInitialized(ref _cacheLoad, () => Task.Run(LoadPersistedCache));

    public void Prefetch(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId) || TryGetCached(videoId, out _))
        {
            return;
        }

        _ = PrefetchAsync(videoId);
    }

    public async Task<string?> GetBackendVersionAsync(CancellationToken cancellationToken = default)
    {
        var exe = ExecutablePath;
        return exe is not null && File.Exists(exe) ? await GetVersionAsync(exe, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Installs the latest yt-dlp into a new version folder when it is newer than the managed build (the return value).
    /// The managed Deno is checked at the same time; its result only goes to the log.
    /// </summary>
    public async Task<bool> UpdateBackendAsync(CancellationToken cancellationToken = default)
    {
        var deno = UpdateJsRuntimeQuietlyAsync(cancellationToken);
        try
        {
            return await UpdateYtDlpAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await deno.ConfigureAwait(false);
        }
    }

    /// <summary>Installs the latest yt-dlp into a new version folder when it is newer than the managed build.</summary>
    internal async Task<bool> UpdateYtDlpAsync(CancellationToken cancellationToken)
    {
        if (!UsesManagedExecutable)
        {
            logger.LogInformation("yt-dlp is user-supplied ({Path}); not updating it", ExecutablePath);
            return false;
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var installed = ManagedInstall;
            var latest = await YtDlpInstaller.GetLatestTagAsync(cancellationToken).ConfigureAwait(false);
            if (installed is not null && !YtDlpInstaller.IsNewer(latest, installed.Tag))
            {
                logger.LogInformation("yt-dlp {Tag} is up to date", installed.Tag);
                YtDlpInstaller.RemoveOtherVersions(ManagedRoot, paths.Tools, installed.Tag, logger);
                return false;
            }

            var exe = await YtDlpInstaller.InstallAsync(ManagedRoot, latest, logger, cancellationToken).ConfigureAwait(false);
            _managed = new InstalledYtDlp(latest, exe);
            logger.LogInformation("yt-dlp updated from {Before} to {After}", installed?.Tag ?? "nothing", latest);
            YtDlpInstaller.RemoveOtherVersions(ManagedRoot, paths.Tools, latest, logger);
            return true;
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>
    /// Makes sure yt-dlp exists, downloading the managed build when it is missing.
    /// Returns true when it was downloaded by this call. Throws when a user-supplied path does not exist.
    /// </summary>
    public async Task<bool> EnsureInstalledAsync(CancellationToken cancellationToken = default)
    {
        if (!UsesManagedExecutable)
        {
            return File.Exists(ExecutablePath)
                ? false
                : throw new HushException($"yt-dlp was not found at \"{ExecutablePath}\". Fix the path in Settings, or clear it to let the app manage its own copy.");
        }

        if (ManagedInstall is { } current && File.Exists(current.ExecutablePath))
        {
            return false;
        }

        await _installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another caller may have installed it while we waited.
            _managed = YtDlpInstaller.FindInstalled(ManagedRoot);
            if (_managed is not null)
            {
                return false;
            }

            logger.LogInformation("yt-dlp is missing; downloading the latest release");
            var tag = await YtDlpInstaller.GetLatestTagAsync(cancellationToken).ConfigureAwait(false);
            var exe = await YtDlpInstaller.InstallAsync(ManagedRoot, tag, logger, cancellationToken).ConfigureAwait(false);
            _managed = new InstalledYtDlp(tag, exe);
            YtDlpInstaller.RemoveOtherVersions(ManagedRoot, paths.Tools, tag, logger);
            return true;
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>What still has to be downloaded before playback works; see <see cref="YtDlpMaintenanceService"/>.</summary>
    internal async Task<MissingComponents> GetMissingComponentsAsync(CancellationToken cancellationToken)
    {
        var plan = await _jsRuntimes.PlanAsync(settings.Current.YtDlpJsRuntime, cancellationToken).ConfigureAwait(false);
        return new MissingComponents(
            YtDlp: UsesManagedExecutable && ManagedInstall is null,
            Deno: plan.WantsManagedDeno && DenoInstaller.AssetName is not null,
            HasNodeFallback: plan.Source == JsRuntimeSource.NodeOnPath);
    }

    /// <summary>Downloads the managed Deno when it is missing. Returns true when this call installed it.</summary>
    internal Task<bool> EnsureJsRuntimeInstalledAsync(CancellationToken cancellationToken) => _jsRuntimes.EnsureInstalledAsync(cancellationToken);

    /// <summary>The installed managed Deno's version, or null.</summary>
    internal Version? ManagedDenoVersion => _jsRuntimes.ManagedDeno?.Version;

    /// <summary>
    /// Updates the managed Deno when the current settings use it. Without <paramref name="force"/> this checks at most
    /// weekly. Returns true when it updated.
    /// </summary>
    internal async Task<bool> UpdateJsRuntimeAsync(bool force, CancellationToken cancellationToken)
    {
        var plan = await _jsRuntimes.PlanAsync(settings.Current.YtDlpJsRuntime, cancellationToken).ConfigureAwait(false);
        return plan.Source == JsRuntimeSource.ManagedDeno && await _jsRuntimes.UpdateAsync(force, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateJsRuntimeQuietlyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await UpdateJsRuntimeAsync(force: true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Deno update check failed");
        }
    }

    // The first time something is played while yt-dlp or Deno is still downloading, say why it takes a while.
    private async Task NotifyIfStillSettingUpAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _setupNoticeShown) != 0)
        {
            return;
        }

        // Not while a failed Deno download waits to be retried: nothing would be downloading.
        var missing = await GetMissingComponentsAsync(cancellationToken).ConfigureAwait(false);
        var waits = missing.YtDlp || (missing.BlocksPlayback && !_jsRuntimes.RecentlyFailed);
        if (waits && Interlocked.Exchange(ref _setupNoticeShown, 1) == 0)
        {
            notifications.ShowInfo(
                "Getting playback ready",
                "Downloading what's needed to play music. Your song starts as soon as that's done.");
        }
    }

    private Resolution StartResolution(string videoId)
    {
        var resolution = new Resolution();
        resolution.Task = Task.Run(async () =>
        {
            try
            {
                var stream = await ResolveCoreAsync(videoId, resolution.Cts.Token).ConfigureAwait(false);
                AddToCache(stream);
                return stream;
            }
            finally
            {
                lock (_inFlightGate)
                {
                    if (_inFlight.TryGetValue(videoId, out var current) && ReferenceEquals(current, resolution))
                    {
                        _inFlight.Remove(videoId);
                    }
                }
            }
        });
        return resolution;
    }

    private async Task<ResolvedStream> ResolveCoreAsync(string videoId, CancellationToken cancellationToken)
    {
        var current = settings.Current;

        // On first run yt-dlp and Deno may both still be downloading: wait for them side by side.
        var choosingRuntime = _jsRuntimes.GetForResolveAsync(current.YtDlpJsRuntime, cancellationToken);
        _ = choosingRuntime.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        string exe;
        try
        {
            await EnsureInstalledAsync(cancellationToken).ConfigureAwait(false);
            exe = ExecutablePath ?? throw new HushException("yt-dlp is not installed.");
        }
        catch (Exception ex) when (ex is HushException or HttpRequestException or IOException or UnauthorizedAccessException)
        {
            throw new StreamResolutionException(videoId, $"yt-dlp is not available: {ex.Message}", ex);
        }

        var runtime = await choosingRuntime.ConfigureAwait(false);
        List<string> args =
        [
            "--ignore-config",
            // WebM/Opus first: YouTube's M4A audio is fragmented (DASH) MP4, which Media Foundation opens over
            // HTTP but then jumps straight to MediaEnded without playing (verified 2026-10-07).
            "-f", "bestaudio[ext=webm]/bestaudio[acodec=opus]/bestaudio/best",
            // The HLS manifest is only needed for video; skipping it saves ~0.3 s per resolve.
            "--extractor-args", "youtube:skip=hls",
            "-j",
            "--no-playlist",
            "--no-progress",
            "--cache-dir", Path.Combine(paths.Cache, "yt-dlp"),
        ];
        args.AddRange(runtime.Arguments);
        logger.LogDebug("Resolving {VideoId} with JavaScript runtime: {Runtime}", videoId, runtime.Description);

        var stopwatch = Stopwatch.StartNew();
        ProcessResult result;
        var withAccount = false;
        try
        {
            // The cookie file is the only unencrypted copy of the session: keep it alive just for the process run.
            using var cookieFile = current.UseAccountForStreams && auth.Status == AuthStatus.SignedIn
                ? await cookieFiles.CreateNetscapeCookieFileAsync(cancellationToken).ConfigureAwait(false)
                : null;
            if (cookieFile is not null)
            {
                withAccount = true;
                args.Add("--cookies");
                args.Add(cookieFile.Path);
            }

            args.Add("https://music.youtube.com/watch?v=" + Uri.EscapeDataString(videoId));
            result = await ProcessRunner.RunAsync(exe, args, ResolveTimeout, cancellationToken, _jsRuntimes.GetEnvironment(runtime))
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new StreamResolutionException(videoId, "yt-dlp took too long to find a stream for this track.", ex);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException)
        {
            throw new StreamResolutionException(videoId, $"Could not run yt-dlp: {ex.Message}", ex);
        }

        var runtimeProblem = YtDlpOutput.FindJsRuntimeProblem(result.StandardError);
        if (runtimeProblem is not null)
        {
            logger.LogWarning("yt-dlp reported a JavaScript runtime problem for {VideoId} ({Runtime}): {Problem}", videoId, runtime.Description, runtimeProblem);
            _jsRuntimes.OnRuntimeProblem(runtime);
        }

        if (result.ExitCode != 0)
        {
            logger.LogWarning("yt-dlp failed for {VideoId} (exit {ExitCode}): {StdErr}", videoId, result.ExitCode, result.StandardError.Trim());
            throw new StreamResolutionException(
                videoId,
                runtimeProblem is not null && runtime.Source == JsRuntimeSource.None
                    ? "Playback isn't set up yet: Deno, which YouTube playback needs, couldn't be downloaded. Check your internet connection and try again."
                    : YtDlpOutput.FindError(result.StandardError) ?? $"yt-dlp failed with exit code {result.ExitCode}.");
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            logger.LogDebug("yt-dlp stderr for {VideoId}: {StdErr}", videoId, result.StandardError.Trim());
        }

        var stream = YtDlpOutput.ParseStream(videoId, result.StandardOutput, DateTimeOffset.UtcNow);
        logger.LogInformation(
            "Resolved {VideoId} in {ElapsedMs} ms: {Container}/{Codec} {Bitrate} kbps, expires {ExpiresAt:u}{Account}",
            videoId,
            stopwatch.ElapsedMilliseconds,
            stream.Container,
            stream.Codec,
            stream.BitrateKbps,
            stream.ExpiresAt,
            withAccount ? " (signed in)" : string.Empty);
        return stream;
    }

    private async Task<string?> GetVersionAsync(string exe, CancellationToken cancellationToken)
    {
        try
        {
            var result = await ProcessRunner.RunAsync(exe, ["--ignore-config", "--version"], VersionTimeout, cancellationToken).ConfigureAwait(false);
            var version = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (result.ExitCode == 0 && !string.IsNullOrEmpty(version))
            {
                return version;
            }

            logger.LogWarning("yt-dlp --version failed (exit {ExitCode}): {StdErr}", result.ExitCode, result.StandardError.Trim());
            return null;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or TimeoutException)
        {
            logger.LogWarning(ex, "Could not run {Path} --version", exe);
            return null;
        }
    }

    private async Task PrefetchAsync(string videoId)
    {
        try
        {
            await ResolveAsync(videoId, interactive: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Prefetch failed for {VideoId}", videoId);
        }
    }

    private bool TryGetCached(string videoId, out ResolvedStream stream)
    {
        if (_cache.TryGetValue(videoId, out stream!))
        {
            if (stream.ExpiresAt - ExpiryMargin > DateTimeOffset.UtcNow)
            {
                return true;
            }

            _cache.TryRemove(KeyValuePair.Create(videoId, stream));
        }

        return false;
    }

    private void AddToCache(ResolvedStream stream)
    {
        _cache[stream.VideoId] = stream;
        if (_cache.Count > MaxCacheEntries)
        {
            // Earliest expiry first, so expired entries always go before live ones.
            foreach (var entry in _cache.OrderBy(e => e.Value.ExpiresAt).Take(_cache.Count - (MaxCacheEntries / 2)).ToList())
            {
                _cache.TryRemove(entry);
            }
        }

        ScheduleSave();
    }

    private void LoadPersistedCache()
    {
        try
        {
            if (!File.Exists(CacheFile))
            {
                return;
            }

            var persisted = JsonSerializer.Deserialize<PersistedCache>(File.ReadAllText(CacheFile));
            if (persisted?.Version != PersistedCacheVersion)
            {
                return; // links resolved with a different format selection
            }

            var loaded = 0;
            foreach (var entry in persisted.Streams)
            {
                if (entry.ExpiresAt - ExpiryMargin > DateTimeOffset.UtcNow
                    && Uri.TryCreate(entry.Url, UriKind.Absolute, out var url)
                    && _cache.TryAdd(entry.VideoId, entry.ToStream(url)))
                {
                    loaded++;
                }
            }

            logger.LogDebug("Loaded {Count} cached stream links", loaded);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogDebug(ex, "Ignoring unreadable stream cache {Path}", CacheFile);
        }
    }

    private void ScheduleSave()
    {
        if (Interlocked.Exchange(ref _savePending, 1) == 0)
        {
            _ = SaveLaterAsync();
        }
    }

    private async Task SaveLaterAsync()
    {
        await Task.Delay(SaveDelay).ConfigureAwait(false);
        Volatile.Write(ref _savePending, 0);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var streams = _cache.Values
                .Where(s => s.ExpiresAt - ExpiryMargin > now)
                .Select(PersistedStream.From)
                .ToList();
            var temp = CacheFile + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(new PersistedCache(PersistedCacheVersion, streams))).ConfigureAwait(false);
            File.Move(temp, CacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug(ex, "Could not save the stream cache");
        }
    }

    private sealed record PersistedCache(string Version, List<PersistedStream> Streams);

    private sealed record PersistedStream(
        string VideoId,
        string Url,
        string? Container,
        string? Codec,
        int? BitrateKbps,
        double? DurationSeconds,
        DateTimeOffset ExpiresAt,
        Dictionary<string, string> Headers)
    {
        public static PersistedStream From(ResolvedStream s) => new(
            s.VideoId,
            s.Url.AbsoluteUri,
            s.Container,
            s.Codec,
            s.BitrateKbps,
            s.Duration?.TotalSeconds,
            s.ExpiresAt,
            new Dictionary<string, string>(s.Headers));

        public ResolvedStream ToStream(Uri url) => new()
        {
            VideoId = VideoId,
            Url = url,
            Container = Container,
            Codec = Codec,
            BitrateKbps = BitrateKbps,
            Duration = DurationSeconds is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            ExpiresAt = ExpiresAt,
            Headers = Headers,
        };
    }

    private sealed class Resolution
    {
        public CancellationTokenSource Cts { get; } = new();

        public Task<ResolvedStream> Task { get; set; } = null!;

        public int Waiters { get; set; }
    }
}
