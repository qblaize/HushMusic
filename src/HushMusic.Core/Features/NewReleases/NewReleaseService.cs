using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.NewReleases;

/// <summary>
/// <see cref="INewReleaseAlerts"/>: runs <see cref="NewReleasePlanner"/>'s schedule while the setting is on and an account
/// is signed in, one request at a time and far apart, and keeps its progress in <c>new-releases.json</c> so a restart picks
/// up where it stopped.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Off or signed out, it only listens for the setting and the sign-in: no timers, no requests.</item>
/// <item>A round starts <see cref="StartDelay"/> after startup (or after the setting is turned on); each request (a page
/// of followed artists, or one artist's page) comes 20 to 30 seconds after the last.</item>
/// <item>Turning the setting off or signing out cancels the request in flight. Signing out also deletes the file, so the
/// next account starts from scratch, silently.</item>
/// <item>Network trouble pauses the round for <see cref="RetryAfterFailure"/>.</item>
/// </list>
/// </remarks>
public sealed partial class NewReleaseService : INewReleaseAlerts, IHostedService, IDisposable
{
    public const string FileName = "new-releases.json";

    internal static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(30);

    /// <summary>Pages of followed artists read at most (ytmusicapi reads them all; this keeps one refresh short).</summary>
    internal const int MaxFollowedPages = 20;

    /// <summary>Artist pages failing in a row before the round pauses (one failure is that artist's; several are the network's).</summary>
    internal const int FailuresBeforePause = 3;

    // Waits are never shorter than this, so a clock change can't make the loop spin.
    private static readonly TimeSpan MinimumWait = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILibraryApi _library;
    private readonly IBrowseApi _browse;
    private readonly IAuthService _auth;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<NewReleaseService> _logger;
    private readonly SemaphoreSlim _control = new(1, 1);

    // Guarded by _control.
    private CancellationTokenSource? _runCancel;
    private Task? _run;
    private bool _stopped;

    // The running round only.
    private int _failuresInRow;
    private bool _saveFailureLogged;

    public NewReleaseService(
        ILibraryApi library,
        IBrowseApi browse,
        IAuthService auth,
        ISettingsService settings,
        IAppPaths paths,
        TimeProvider time,
        ILogger<NewReleaseService> logger)
    {
        _library = library;
        _browse = browse;
        _auth = auth;
        _settings = settings;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    public event EventHandler<NewReleasesFoundEventArgs>? Found;

    /// <summary>The wait before a round's first request.</summary>
    internal TimeSpan FirstDelay { get; init; } = StartDelay;

    /// <summary>The shortest and longest pause between two requests.</summary>
    internal TimeSpan MinGap { get; init; } = TimeSpan.FromSeconds(20);

    /// <inheritdoc cref="MinGap"/>
    internal TimeSpan MaxGap { get; init; } = TimeSpan.FromSeconds(30);

    internal int MaxChecksPerDay { get; init; } = NewReleasePlanner.MaxChecksPerDay;

    /// <summary>The running round, or null while off. Tests wait on it.</summary>
    internal Task? Run => _run;

    internal string FilePath => Path.Combine(_paths.Root, FileName);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.Changed += OnSettingsChanged;
        _auth.StatusChanged += OnAuthStatusChanged;
        return ApplyAsync(signedOut: false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        _auth.StatusChanged -= OnAuthStatusChanged;
        _stopped = true;
        await Task.WhenAny(ApplyAsync(signedOut: false), Task.Delay(StopTimeout, CancellationToken.None)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _stopped = true;
        _runCancel?.Cancel();
    }

    /// <summary>Starts or stops the round to match the setting and the sign-in; deletes the file after a sign-out.</summary>
    internal async Task ApplyAsync(bool signedOut)
    {
        await _control.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_run is { IsCompleted: true })
            {
                // It ended by itself (signed out mid-request, or broke): a later change may start a new one.
                await EndRunAsync().ConfigureAwait(false);
            }

            var wanted = !_stopped && _settings.Current.NewReleaseAlerts && _auth.Status == AuthStatus.SignedIn;
            if (wanted && _run is null)
            {
                _runCancel = new CancellationTokenSource();
                var token = _runCancel.Token;
                _run = Task.Run(() => RunAsync(token), CancellationToken.None);
                LogStarted(_logger);
            }
            else if (!wanted && _run is not null)
            {
                await EndRunAsync().ConfigureAwait(false);
                LogStopped(_logger);
            }

            if (signedOut)
            {
                Forget();
            }
        }
        finally
        {
            _control.Release();
        }
    }

    // Under _control.
    private async Task EndRunAsync()
    {
        await _runCancel!.CancelAsync().ConfigureAwait(false);
        await _run!.ConfigureAwait(false);
        _runCancel.Dispose();
        _runCancel = null;
        _run = null;
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => _ = ApplyAsync(signedOut: false);

    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e) => _ = ApplyAsync(e.Status == AuthStatus.SignedOut);

    // Never throws: the round ends quietly when cancelled or signed out.
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        NewReleaseState? state = null;
        _failuresInRow = 0;
        try
        {
            await Task.Delay(FirstDelay, _time, cancellationToken).ConfigureAwait(false);
            state = await LoadAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var now = _time.GetUtcNow();
                var pause = NewReleasePlanner.Next(state, now, MaxChecksPerDay) switch
                {
                    NewReleasePlanner.WaitUntil wait => await IdleAsync(state, wait.Until, now, cancellationToken).ConfigureAwait(false),
                    NewReleasePlanner.CheckArtist check => await CheckAsync(state, check.ArtistId, cancellationToken).ConfigureAwait(false),
                    _ => await RefreshFollowedAsync(state, cancellationToken).ConfigureAwait(false),
                };

                await Task.Delay(pause, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AuthRequiredException)
        {
            LogSignedOut(_logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "New-release alerts stopped");
        }
        finally
        {
            // Turned off: what was found but not announced stays unannounced.
            if (state is { Pending.Count: > 0 } && !_settings.Current.NewReleaseAlerts)
            {
                state.Pending.Clear();
                await SaveAsync(state, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    // Nothing due: announce what the round found, then sleep until the next step.
    private async Task<TimeSpan> IdleAsync(NewReleaseState state, DateTimeOffset until, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (state.Pending.Count > 0)
        {
            var releases = NewReleasePlanner.TakePending(state, now);
            await SaveAsync(state, cancellationToken).ConfigureAwait(false);
            Announce(releases);
        }

        LogIdle(_logger, state.Artists.Count, until);
        return until - now > MinimumWait ? until - now : MinimumWait;
    }

    private async Task<TimeSpan> RefreshFollowedAsync(NewReleaseState state, CancellationToken cancellationToken)
    {
        try
        {
            var followed = await GetFollowedAsync(cancellationToken).ConfigureAwait(false);
            NewReleasePlanner.SetFollowed(state, followed, _time.GetUtcNow());
            LogFollowed(_logger, state.Artists.Count);
        }
        catch (Exception ex) when (ex is not AuthRequiredException && !cancellationToken.IsCancellationRequested)
        {
            LogPaused(_logger, ex, RetryAfterFailure);
            return RetryAfterFailure;
        }

        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return Gap();
    }

    // One artist's page; the pause before the next step is long when pages keep failing.
    private async Task<TimeSpan> CheckAsync(NewReleaseState state, string artistId, CancellationToken cancellationToken)
    {
        try
        {
            var page = await _browse.GetArtistAsync(artistId, cancellationToken).ConfigureAwait(false);
            var found = NewReleasePlanner.RecordCheck(state, artistId, page, _time.GetUtcNow());
            _failuresInRow = 0;
            if (found.Count > 0)
            {
                LogFound(_logger, found.Count, page.Artist.Title);
            }
        }
        catch (Exception ex) when (ex is not AuthRequiredException && !cancellationToken.IsCancellationRequested)
        {
            if (++_failuresInRow >= FailuresBeforePause)
            {
                _failuresInRow = 0;
                LogPaused(_logger, ex, RetryAfterFailure);
                return RetryAfterFailure;
            }

            LogCheckFailed(_logger, ex, artistId);
            NewReleasePlanner.RecordFailedCheck(state, artistId, _time.GetUtcNow());
        }

        await SaveAsync(state, cancellationToken).ConfigureAwait(false);
        return Gap();
    }

    private async Task<List<Artist>> GetFollowedAsync(CancellationToken cancellationToken)
    {
        var artists = new List<Artist>();
        string? continuation = null;
        var pages = 0;
        do
        {
            if (pages > 0)
            {
                await Task.Delay(Gap(), _time, cancellationToken).ConfigureAwait(false);
            }

            var page = await _library.GetSubscriptionsAsync(continuation, cancellationToken).ConfigureAwait(false);
            artists.AddRange(page.Items);
            continuation = page.Continuation;
        }
        while (continuation is not null && ++pages < MaxFollowedPages);

        return artists;
    }

    private void Announce(IReadOnlyList<PendingRelease> releases)
    {
        if (releases.Count == 0)
        {
            return;
        }

        LogAnnounced(_logger, releases.Count);
        try
        {
            Found?.Invoke(this, new NewReleasesFoundEventArgs([.. releases.Select(r => r.ToRelease())]));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A new-release handler failed");
        }
    }

    private TimeSpan Gap() => MinGap + ((MaxGap - MinGap) * Random.Shared.NextDouble());

    private async Task<NewReleaseState> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(FilePath))
            {
                await using var stream = File.OpenRead(FilePath);
                if (await JsonSerializer.DeserializeAsync<NewReleaseState>(stream, JsonOptions, cancellationToken).ConfigureAwait(false) is { } state)
                {
                    state.Artists.RemoveAll(a => string.IsNullOrEmpty(a.Id));
                    return state;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The new-release file is unreadable; starting over");
        }

        return new NewReleaseState();
    }

    // Progress that can't be saved is only lost on a restart: keep going.
    private async Task SaveAsync(NewReleaseState state, CancellationToken cancellationToken)
    {
        var temp = FilePath + ".tmp";
        try
        {
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!_saveFailureLogged)
            {
                _saveFailureLogged = true;
                _logger.LogWarning(ex, "Could not save the new-release file");
            }
        }
    }

    private void Forget()
    {
        try
        {
            File.Delete(FilePath);
            File.Delete(FilePath + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not delete the new-release file");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "New-release alerts on")]
    private static partial void LogStarted(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "New-release alerts off")]
    private static partial void LogStopped(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "New releases: following {Count} artists")]
    private static partial void LogFollowed(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "New releases: {Count} artists followed; next step at {Until}")]
    private static partial void LogIdle(ILogger logger, int count, DateTimeOffset until);

    [LoggerMessage(Level = LogLevel.Information, Message = "New releases: {Count} new from {Artist}")]
    private static partial void LogFound(ILogger logger, int count, string artist);

    [LoggerMessage(Level = LogLevel.Information, Message = "New releases: announcing {Count}")]
    private static partial void LogAnnounced(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "New releases: could not check artist {ArtistId}; trying again tomorrow")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, string artistId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "New releases: requests are failing; pausing for {Pause}")]
    private static partial void LogPaused(ILogger logger, Exception exception, TimeSpan pause);

    [LoggerMessage(Level = LogLevel.Debug, Message = "New releases: signed out; stopping")]
    private static partial void LogSignedOut(ILogger logger);
}

public static class NewReleaseServiceCollectionExtensions
{
    /// <summary>New-release alerts for followed artists (off until the user turns them on).</summary>
    public static IServiceCollection AddNewReleaseAlerts(this IServiceCollection services)
    {
        services.AddSingleton<NewReleaseService>();
        services.AddSingleton<INewReleaseAlerts>(sp => sp.GetRequiredService<NewReleaseService>());
        services.AddHostedService(sp => sp.GetRequiredService<NewReleaseService>());
        return services;
    }
}
