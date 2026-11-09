using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Playback.YtDlp;

namespace HushMusic.Playback;

/// <summary>
/// Resolves audio stream URLs by running yt-dlp.exe. Results are cached until shortly before they expire,
/// and concurrent requests for the same track share one yt-dlp process. Each run also lists the track's video formats,
/// kept in memory for <see cref="ResolveVideoAsync"/>. yt-dlp's JavaScript runtime (normally the
/// app's own Deno) comes from <see cref="JsRuntimeProvider"/>.
/// </summary>
/// <remarks>
/// Each resolve asks yt-dlp's visionOS client first, without the account: its links work at once. Only when that finds
/// nothing does yt-dlp run with its default clients (and the account, when "Use my account when resolving streams" is
/// on). Their links are held back for a few seconds (<see cref="ResolvedStream.AvailableAt"/>); the public resolve
/// methods wait for that, prefetching doesn't. A track YouTube age-gates is remembered for the session, so it isn't
/// asked about again on every prefetch, preload and play.
/// </remarks>
public sealed class YtDlpStreamResolver(
    ISettingsService settings,
    IAppPaths paths,
    IAuthService auth,
    ICookieFileProvider cookieFiles,
    INotificationService notifications,
    ILogger<YtDlpStreamResolver> logger) : IStreamResolver
{
    private const int MaxCacheEntries = 256;
    private const int MaxVideoEntries = 64;
    private static readonly TimeSpan ResolveTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan AvailabilityMargin = TimeSpan.FromSeconds(1);

    // yt-dlp's player client for the first try of each resolve (see ResolveCoreAsync).
    private const string VisionOsClient = "visionos";

    // Bump when the yt-dlp format selection changes, so links resolved with the old one are not reused.
    private const string PersistedCacheVersion = "webm-2";

    private readonly ConcurrentDictionary<string, ResolvedStream> _cache = new(StringComparer.Ordinal);

    // The playable video formats each run listed (memory only; empty when a track has none).
    private readonly ConcurrentDictionary<string, IReadOnlyList<ResolvedStream>> _videoFormats = new(StringComparer.Ordinal);

    // Tracks YouTube age-gated this session, and whether yt-dlp had the account then (memory only).
    private readonly ConcurrentDictionary<string, bool> _ageRestricted = new(StringComparer.Ordinal);

    // Age-restricted track -> the counterpart that plays in its place.
    private readonly ConcurrentDictionary<string, string> _counterparts = new(StringComparer.Ordinal);
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

    /// <summary>Whether yt-dlp runs with the account cookies now ("Use my account when resolving streams", signed in).</summary>
    private bool UsesAccount => settings.Current.UseAccountForStreams && auth.Status == AuthStatus.SignedIn;

    public async Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var stream = await ResolveAsync(videoId, interactive: true, cancellationToken).ConfigureAwait(false);
        await WaitUntilAvailableAsync(stream, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    /// <summary>
    /// Resolves the track; when YouTube only plays it to a signed-in, age-confirmed account, its Song / Video counterpart
    /// is tried before giving up. YouTube Music often keeps an audio-only song next to an age-restricted music video,
    /// and the two aren't always rated alike. (Queues only name counterparts when signed in.)
    /// </summary>
    public async Task<ResolvedStream> ResolveAsync(Track track, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        StreamResolutionException restricted;
        try
        {
            return await ResolveAsync(track.VideoId, cancellationToken).ConfigureAwait(false);
        }
        catch (StreamResolutionException ex) when (ex.Reason == StreamFailureReason.AgeRestricted && Counterpart(track) is not null)
        {
            restricted = ex;
        }

        var counterpart = Counterpart(track)!;
        try
        {
            var stream = await ResolveAsync(counterpart.VideoId, cancellationToken).ConfigureAwait(false);
            _counterparts[track.VideoId] = counterpart.VideoId;
            logger.LogInformation(
                "{VideoId} is age-restricted; playing its {Kind} {Counterpart} instead",
                track.VideoId,
                counterpart.IsMusicVideo ? "music video" : "song",
                counterpart.VideoId);
            return stream;
        }
        catch (StreamResolutionException ex)
        {
            logger.LogDebug("The counterpart {Counterpart} of age-restricted {VideoId} doesn't play either: {Error}", counterpart.VideoId, track.VideoId, ex.Message);
        }

        throw restricted;
    }

    private static Track? Counterpart(Track track) =>
        track.Counterpart is { IsLiveRadio: false, VideoId.Length: > 0 } counterpart && counterpart.VideoId != track.VideoId ? counterpart : null;

    // A fresh link only works from AvailableAt on (googlevideo answers 403 before, which Media Foundation reports as
    // 0xC00D2EE7, "server name not found"). The time is whole seconds and the server counts from when it answered, so a
    // second more is always enough. Prefetched links are usually past it by the time they play.
    private async Task WaitUntilAvailableAsync(ResolvedStream stream, CancellationToken cancellationToken)
    {
        var wait = stream.AvailableAt is { } at ? at + AvailabilityMargin - DateTimeOffset.UtcNow : TimeSpan.Zero;
        if (wait > TimeSpan.Zero)
        {
            logger.LogDebug("Waiting {Wait} ms until YouTube serves {VideoId}", (int)wait.TotalMilliseconds, stream.VideoId);
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }

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

        return await RunAsync(videoId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The music video's picture, picked from the formats the last yt-dlp run for the track listed (an audio resolve
    /// brings them along). Only when the audio link came from the disk cache does this run yt-dlp itself.
    /// </summary>
    public async Task<ResolvedStream> ResolveVideoAsync(string videoId, int maxHeight, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        if (!TryGetVideoFormats(videoId, out var formats))
        {
            await RunAsync(videoId, cancellationToken).ConfigureAwait(false);
            if (!TryGetVideoFormats(videoId, out formats))
            {
                throw new StreamResolutionException(videoId, "yt-dlp listed no video for this track.");
            }
        }

        var stream = VideoFormats.Pick(formats, maxHeight)
            ?? throw new StreamResolutionException(videoId, "This video has no picture in a format Windows can play.");
        logger.LogDebug(
            "Video for {VideoId}: {Width}x{Height} {Codec} {FrameRate} fps, {Bitrate} kbps (at most {MaxHeight}p)",
            videoId,
            stream.Width,
            stream.Height,
            stream.Codec,
            stream.FrameRate,
            stream.BitrateKbps,
            maxHeight);
        await WaitUntilAvailableAsync(stream, cancellationToken).ConfigureAwait(false);
        return stream;
    }

    // Runs yt-dlp for the track, or joins the run already going for it. A track YouTube age-gated this session isn't
    // asked about again until yt-dlp would ask differently (with or without the account).
    private async Task<ResolvedStream> RunAsync(string videoId, CancellationToken cancellationToken)
    {
        if (IsKnownAgeRestricted(videoId, out var withAccount))
        {
            throw AgeRestricted(videoId, withAccount);
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
        _videoFormats.TryRemove(videoId, out _);
        var removed = _cache.TryRemove(videoId, out _);

        // An age-restricted track plays its counterpart's link: that is the one that went stale.
        if (_counterparts.TryRemove(videoId, out var counterpart))
        {
            _videoFormats.TryRemove(counterpart, out _);
            removed |= _cache.TryRemove(counterpart, out _);
        }

        if (removed)
        {
            ScheduleSave();
        }
    }

    /// <summary>Loads links resolved in earlier sessions (they stay valid for ~6 h). Safe to call repeatedly.</summary>
    public Task EnsureCacheLoadedAsync() => LazyInitializer.EnsureInitialized(ref _cacheLoad, () => Task.Run(LoadPersistedCache));

    public void Prefetch(string videoId)
    {
        if (string.IsNullOrWhiteSpace(videoId) || TryGetCached(videoId, out _) || IsKnownAgeRestricted(videoId, out _))
        {
            return;
        }

        _ = PrefetchAsync(videoId);
    }

    /// <summary>
    /// What to tell the user when YouTube age-gates a track: how to play it from here (sign in, or let yt-dlp use the
    /// account), or, when yt-dlp already used the account, that YouTube wants the age confirmed on it.
    /// </summary>
    internal static string AgeRestrictedMessage(bool signedIn, bool usedAccount) =>
        usedAccount ? "This song is age-restricted on YouTube, and YouTube didn't accept your account for it. Confirm your age on YouTube with this Google account, then try again."
        : signedIn ? "This song is age-restricted on YouTube. To play it, turn on \"Use my account when resolving streams\" in Settings → Playback."
        : "This song is age-restricted on YouTube. To play it, sign in, then turn on \"Use my account when resolving streams\" in Settings → Playback.";

    private StreamResolutionException AgeRestricted(string videoId, bool usedAccount) =>
        new(videoId, AgeRestrictedMessage(auth.Status == AuthStatus.SignedIn, usedAccount), reason: StreamFailureReason.AgeRestricted);

    // Known when it was age-gated this session the way yt-dlp would run now: with the account, or without it.
    private bool IsKnownAgeRestricted(string videoId, out bool withAccount) =>
        _ageRestricted.TryGetValue(videoId, out withAccount) && withAccount == UsesAccount;

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
        logger.LogDebug("Resolving {VideoId} with JavaScript runtime: {Runtime}", videoId, runtime.Description);
        var stopwatch = Stopwatch.StartNew();

        // The visionOS client first, and without the account. It is one of yt-dlp's default clients and lists the same
        // WebM/Opus audio and VP9 video, but needs no player JavaScript (about a second faster), and its links work at
        // once: the TV client's are held back for the length of a pre-roll ad (~5 s, see AvailableAt).
        var quick = await RunYtDlpAsync(videoId, exe, runtime, VisionOsClient, useAccount: false, cancellationToken).ConfigureAwait(false);
        if (quick.Info is { } quickInfo && YtDlpOutput.HasPlayableAudio(quickInfo))
        {
            return Resolved(videoId, quickInfo, stopwatch, "visionOS", withAccount: false);
        }

        // Age-gated for every client alike: only the account can change the answer.
        var useAccount = UsesAccount;
        if (quick.Reason == StreamFailureReason.AgeRestricted && !useAccount)
        {
            _ageRestricted[videoId] = false;
            throw AgeRestricted(videoId, usedAccount: false);
        }

        logger.LogInformation(
            "The visionOS client found nothing to play for {VideoId}; asking yt-dlp's default clients{Account}",
            videoId,
            useAccount ? " with the account" : string.Empty);
        var full = await RunYtDlpAsync(videoId, exe, runtime, clients: null, useAccount, cancellationToken).ConfigureAwait(false);
        if (full.Info is not { } info)
        {
            if (full.Reason == StreamFailureReason.AgeRestricted)
            {
                // Prefetch, preload and play would otherwise each spend yt-dlp runs on the same answer.
                _ageRestricted[videoId] = full.WithAccount;
                throw AgeRestricted(videoId, full.WithAccount);
            }

            throw new StreamResolutionException(videoId, full.Error!);
        }

        return Resolved(videoId, info, stopwatch, "default clients", full.WithAccount);
    }

    /// <summary>
    /// One yt-dlp run: with <paramref name="clients"/> (its <c>player_client</c> list; null for yt-dlp's defaults), and the
    /// account cookies when <paramref name="useAccount"/>. Returns the info JSON, or why there is none.
    /// </summary>
    private async Task<YtDlpRun> RunYtDlpAsync(
        string videoId,
        string exe,
        JsRuntimePlan runtime,
        string? clients,
        bool useAccount,
        CancellationToken cancellationToken)
    {
        List<string> args =
        [
            "--ignore-config",
            // WebM/Opus first: YouTube's M4A audio is fragmented (DASH) MP4, which Media Foundation opens over
            // HTTP but then jumps straight to MediaEnded without playing (verified 2026-10-07). The selection is checked
            // again against the full format list (AudioFormats), which also gives the fallbacks.
            "-f", "bestaudio[ext=webm]/bestaudio[acodec=opus]/bestaudio/best",
            // Skipping the HLS manifest saves ~0.3 s per resolve. Video comes from the https (DASH) formats, which stay.
            // (YouTube's HLS audio is packed AAC, which Media Foundation's HLS source doesn't play anyway.)
            "--extractor-args", clients is null ? "youtube:skip=hls" : $"youtube:skip=hls;player_client={clients}",
            "-j",
            "--no-playlist",
            "--no-progress",
            "--cache-dir", Path.Combine(paths.Cache, "yt-dlp"),
        ];
        args.AddRange(runtime.Arguments);

        ProcessResult result;
        var withAccount = false;
        try
        {
            // The cookie file is the only unencrypted copy of the session: keep it alive just for the process run.
            using var cookieFile = useAccount
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
            logger.LogWarning(
                "yt-dlp failed for {VideoId} ({Clients}, exit {ExitCode}): {StdErr}",
                videoId,
                clients ?? "default clients",
                result.ExitCode,
                result.StandardError.Trim());
            var error = runtimeProblem is not null && runtime.Source == JsRuntimeSource.None
                ? "Playback isn't set up yet: Deno, which YouTube playback needs, couldn't be downloaded. Check your internet connection and try again."
                : YtDlpOutput.FindError(result.StandardError) ?? $"yt-dlp failed with exit code {result.ExitCode}.";
            return new YtDlpRun(null, YtDlpOutput.ClassifyError(result.StandardError), error, withAccount);
        }

        if (!string.IsNullOrWhiteSpace(result.StandardError))
        {
            logger.LogDebug("yt-dlp stderr for {VideoId}: {StdErr}", videoId, result.StandardError.Trim());
        }

        try
        {
            return new YtDlpRun(YtDlpOutput.ParseInfo(videoId, result.StandardOutput), StreamFailureReason.Other, null, withAccount);
        }
        catch (StreamResolutionException ex)
        {
            return new YtDlpRun(null, StreamFailureReason.Other, ex.Message, withAccount);
        }
    }

    private ResolvedStream Resolved(string videoId, JsonObject info, Stopwatch stopwatch, string client, bool withAccount)
    {
        var now = DateTimeOffset.UtcNow;
        var stream = YtDlpOutput.ParseAudio(videoId, info, now);
        RememberVideoFormats(videoId, YtDlpOutput.ParseVideoFormats(videoId, info, now));
        _ageRestricted.TryRemove(videoId, out _);
        logger.LogInformation(
            "Resolved {VideoId} in {ElapsedMs} ms ({Client}): {Container}/{Codec} {Bitrate} kbps{Fallback}, expires {ExpiresAt:u}{HeldBack}{Account}",
            videoId,
            stopwatch.ElapsedMilliseconds,
            client,
            stream.Container,
            stream.Codec,
            stream.BitrateKbps,
            stream.Fallback is { } fallback ? $" (else {fallback.Container}/{fallback.Codec})" : string.Empty,
            stream.ExpiresAt,
            stream.AvailableAt is { } at && at > now ? $", served from {(at - now).TotalSeconds:0} s on" : string.Empty,
            withAccount ? " (signed in)" : string.Empty);
        return stream;
    }

    /// <summary>A yt-dlp run's info JSON, or why there is none (<paramref name="Error"/> is the message for the user).</summary>
    private sealed record YtDlpRun(JsonObject? Info, StreamFailureReason Reason, string? Error, bool WithAccount);

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
        catch (StreamResolutionException ex) when (ex.Reason == StreamFailureReason.AgeRestricted)
        {
            logger.LogInformation("Prefetch: {VideoId} is age-restricted", videoId);
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

    private bool TryGetVideoFormats(string videoId, out IReadOnlyList<ResolvedStream> formats)
    {
        if (_videoFormats.TryGetValue(videoId, out formats!))
        {
            if (formats.Count == 0 || formats.Min(f => f.ExpiresAt) - ExpiryMargin > DateTimeOffset.UtcNow)
            {
                return true;
            }

            _videoFormats.TryRemove(KeyValuePair.Create(videoId, formats));
        }

        return false;
    }

    private void RememberVideoFormats(string videoId, IReadOnlyList<ResolvedStream> formats)
    {
        _videoFormats[videoId] = formats;
        if (_videoFormats.Count > MaxVideoEntries)
        {
            // Tracks without a video and the earliest expiry go first.
            foreach (var entry in _videoFormats
                .OrderBy(e => e.Value.Count == 0 ? DateTimeOffset.MinValue : e.Value.Min(f => f.ExpiresAt))
                .Take(_videoFormats.Count - (MaxVideoEntries / 2))
                .ToList())
            {
                _videoFormats.TryRemove(entry);
            }
        }
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
