using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.Discord;

/// <summary>
/// <see cref="IDiscordPresence"/>: follows the player and the song on air, and keeps the Discord desktop app's
/// "Listening to" status in step through <see cref="DiscordIpcConnection"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item>Off (or without a real application id), it only listens for the setting: no events, timers, threads or pipes.</item>
/// <item>On, one background loop sends the latest status (<see cref="PresenceCoalescer"/>). It connects only when there is
/// something to show, so with Discord closed it tries again every 30 to 60 seconds, and only while something plays.</item>
/// <item>Paused for <see cref="PauseGrace"/>, stopped, or nothing loaded: the status is cleared. A seek moves the progress
/// bar (a position more than <see cref="SeekTolerance"/> off where the bar is counts as one).</item>
/// </list>
/// </remarks>
public sealed class DiscordPresenceService : IDiscordPresence, IHostedService, IDisposable
{
    internal static readonly TimeSpan PauseGrace = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan SeekTolerance = TimeSpan.FromSeconds(3);
    internal static readonly TimeSpan RetryMin = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan RetryMax = TimeSpan.FromSeconds(60);

    // Discord refused the application id: no point asking again soon.
    internal static readonly TimeSpan RejectedRetry = TimeSpan.FromMinutes(10);

    // While a station's song has no cover of its own, the cache of matched songs is looked at this often.
    private static readonly TimeSpan CoverRecheck = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ClearTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private readonly IPlayer _player;
    private readonly IRadioNowPlaying _radio;
    private readonly ISettingsService _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<DiscordPresenceService> _logger;
    private readonly DiscordPresenceOptions _options;
    private readonly IRadioTrackMatcher? _matcher;
    private readonly Lock _gate = new();
    private readonly PresenceCoalescer _coalescer = new();

    // Guarded by _gate.
    private TaskCompletionSource _changed = NewSignal();
    private CancellationTokenSource? _active;
    private Task _worker = Task.CompletedTask;
    private ITimer? _pauseTimer;
    private string? _desired;
    private string? _anchorKey;
    private DateTimeOffset _anchor;
    private TimeSpan? _duration;
    private PlaybackStatus _lastStatus;
    private DateTimeOffset? _pausedAt;
    private DateTimeOffset _nextCoverCheck;
    private bool _coverSettled;
    private bool _placeholderLogged;

    // Worker only.
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    private int _connectFailures;

    private volatile bool _connected;

    public DiscordPresenceService(
        IPlayer player,
        IRadioNowPlaying radio,
        ISettingsService settings,
        TimeProvider time,
        ILogger<DiscordPresenceService> logger,
        DiscordPresenceOptions options,
        IRadioTrackMatcher? matcher = null)
    {
        _player = player;
        _radio = radio;
        _settings = settings;
        _time = time;
        _logger = logger;
        _options = options;
        _matcher = matcher;
    }

    public event EventHandler? ConnectionChanged;

    public bool IsConnected => _connected;

    /// <summary>The loop that talks to Discord (completed while off). Tests wait on it.</summary>
    internal Task Worker
    {
        get
        {
            lock (_gate)
            {
                return _worker;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _settings.Changed += OnSettingsChanged;
        Apply();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _settings.Changed -= OnSettingsChanged;
        Task worker;
        lock (_gate)
        {
            worker = Deactivate();
        }

        await Task.WhenAny(worker, Task.Delay(StopTimeout, CancellationToken.None)).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Deactivate();
        }
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnSettingsChanged(object? sender, EventArgs e) => Apply();

    private void Apply()
    {
        var on = _settings.Current.ShowOnDiscord && _options.Allowed;
        var configured = DiscordPresence.IsValidApplicationId(_options.ApplicationId);
        lock (_gate)
        {
            if (on && !configured)
            {
                if (!_placeholderLogged)
                {
                    _placeholderLogged = true;
                    _logger.LogDebug("Discord status is on, but no Discord application id is set; nothing is sent");
                }

                on = false;
            }

            if (on == (_active is not null))
            {
                return;
            }

            if (on)
            {
                Activate();
            }
            else
            {
                Deactivate();
            }
        }
    }

    // Under _gate.
    private void Activate()
    {
        _active = new CancellationTokenSource();
        _pauseTimer = _time.CreateTimer(_ => OnPauseGraceOver(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _player.TrackChanged += OnTrackChanged;
        _player.TrackStarted += OnTrackStarted;
        _player.StatusChanged += OnStatusChanged;
        _player.PositionChanged += OnPositionChanged;
        _radio.Changed += OnRadioChanged;

        _desired = null;
        _anchorKey = null;
        _pausedAt = null;
        _lastStatus = _player.Status;
        Refresh();

        var token = _active.Token;
        _worker = Task.Run(() => RunAsync(token), CancellationToken.None);
        _logger.LogInformation("Discord status on");
    }

    // Under _gate. Returns the loop, which clears the status and hangs up as it ends.
    private Task Deactivate()
    {
        if (_active is null)
        {
            return _worker;
        }

        _player.TrackChanged -= OnTrackChanged;
        _player.TrackStarted -= OnTrackStarted;
        _player.StatusChanged -= OnStatusChanged;
        _player.PositionChanged -= OnPositionChanged;
        _radio.Changed -= OnRadioChanged;
        _pauseTimer?.Dispose();
        _pauseTimer = null;
        // Cancelled, not disposed: the loop may still be linking to its token on the way out.
        _active.Cancel();
        _active = null;
        _changed.TrySetResult();
        _logger.LogInformation("Discord status off");
        return _worker;
    }

    // ===== Playback (events arrive on the player's and the radio's background threads) =====

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            _anchorKey = null;
            Refresh();
        }
    }

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        lock (_gate)
        {
            // Audio really started (also a repeat of the same song): the bar starts from here.
            _anchorKey = null;
            Refresh();
        }
    }

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            var wasPlaying = _lastStatus is PlaybackStatus.Playing or PlaybackStatus.Buffering or PlaybackStatus.Loading;
            _lastStatus = e.Status;
            if (e.Status == PlaybackStatus.Paused)
            {
                if (wasPlaying)
                {
                    _pausedAt = now;
                    _pauseTimer?.Change(PauseGrace, Timeout.InfiniteTimeSpan);
                }
            }
            else
            {
                if (e.Status == PlaybackStatus.Playing && _player.CurrentTrack is { } track)
                {
                    // A song continues from its position; a station that was cleared starts over.
                    if (!track.IsLiveRadio || _pausedAt is null || now - _pausedAt >= PauseGrace)
                    {
                        _anchorKey = null;
                    }
                }

                _pausedAt = null;
                _pauseTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }

            Refresh();
        }
    }

    private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        lock (_gate)
        {
            if (_active is null || _player.CurrentTrack is not { } track || track.VideoId != _anchorKey)
            {
                return;
            }

            var now = _time.GetUtcNow();
            if (track.IsLiveRadio)
            {
                if (!_coverSettled && now >= _nextCoverCheck)
                {
                    _nextCoverCheck = now + CoverRecheck;
                    Refresh();
                }

                return;
            }

            var duration = e.Duration > TimeSpan.Zero ? e.Duration : (TimeSpan?)null;
            var moved = (now - e.Position - _anchor).Duration() > SeekTolerance;
            var lengthKnown = duration is { } d && (_duration is not { } known || (d - known).Duration() > TimeSpan.FromSeconds(1));
            if (moved || lengthKnown)
            {
                _anchor = now - e.Position;
                _duration = duration ?? _duration;
                Refresh();
            }
        }
    }

    private void OnRadioChanged(object? sender, RadioNowPlayingChangedEventArgs e)
    {
        lock (_gate)
        {
            _coverSettled = false;
            Refresh();
        }
    }

    private void OnPauseGraceOver()
    {
        lock (_gate)
        {
            Refresh();
        }
    }

    // Under _gate: works out what Discord should show and hands a change to the loop.
    private void Refresh()
    {
        if (_active is null)
        {
            return;
        }

        var desired = Compute()?.ToJsonString();
        if (string.Equals(desired, _desired, StringComparison.Ordinal))
        {
            return;
        }

        _desired = desired;
        _coalescer.Set(desired);
        _changed.TrySetResult();
    }

    // Under _gate.
    private JsonObject? Compute()
    {
        if (_player.CurrentTrack is not { } track)
        {
            return null;
        }

        var now = _time.GetUtcNow();
        var showing = _player.Status switch
        {
            PlaybackStatus.Playing or PlaybackStatus.Buffering or PlaybackStatus.Loading => true,
            PlaybackStatus.Paused => _pausedAt is { } since && now - since < PauseGrace,
            _ => false,
        };
        if (!showing)
        {
            return null;
        }

        if (_anchorKey != track.VideoId)
        {
            _anchorKey = track.VideoId;
            _anchor = track.IsLiveRadio ? now : now - _player.Position;
            _duration = _player.Duration > TimeSpan.Zero ? _player.Duration : null;
            _coverSettled = false;
            _nextCoverCheck = now + CoverRecheck;
        }

        if (track.Station is { } station)
        {
            var current = _radio.Current;
            var onAir = LiveRadio.IsFor(track, current) ? current : null;
            return DiscordActivity.ForStation(station, onAir, _anchor, StationCover(station, onAir));
        }

        return DiscordActivity.ForSong(track, _anchor, _duration ?? track.Duration);
    }

    // The song's own art from the station, else a confident match already found on YouTube Music (never a new search),
    // else the station's logo.
    private string? StationCover(RadioStation station, RadioNowPlaying? onAir)
    {
        if (onAir is null)
        {
            _coverSettled = true;
            return station.LogoUrl;
        }

        if (DiscordActivity.ImageUrl(onAir.ArtworkUrl) is { } art)
        {
            _coverSettled = true;
            return art;
        }

        if (_matcher?.Remembered(onAir, station.Name) is { Count: > 0 } found
            && RadioAutoSaveRules.PickMatch(onAir, found, station.Name) is { } match
            && DiscordActivity.ImageUrl(DiscordActivity.CoverUrl(match)) is { } matched)
        {
            _coverSettled = true;
            return matched;
        }

        return station.LogoUrl;
    }

    // ===== The loop that talks to Discord =====

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        DiscordIpcConnection? connection = null;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (connection is { Closed.IsCompleted: true })
                {
                    await connection.DisposeAsync().ConfigureAwait(false);
                    connection = null;
                    Disconnected("Discord hung up");
                }

                Task changed;
                var wait = Timeout.InfiniteTimeSpan;
                var connect = false;
                var send = false;
                string? activity = null;
                lock (_gate)
                {
                    if (_changed.Task.IsCompleted)
                    {
                        _changed = NewSignal();
                    }

                    changed = _changed.Task;
                    var now = _time.GetUtcNow();
                    if (!_coalescer.HasPending)
                    {
                        // Nothing to do until the status changes.
                    }
                    else if (connection is null)
                    {
                        if (_coalescer.PendingIsClear)
                        {
                            _coalescer.DropPending();
                        }
                        else if (now < _nextAttempt)
                        {
                            wait = _nextAttempt - now;
                        }
                        else
                        {
                            connect = true;
                        }
                    }
                    else if (_coalescer.TryTake(now, out activity))
                    {
                        send = true;
                    }
                    else if (_coalescer.DueAt(now) is { } due)
                    {
                        wait = due - now;
                    }
                }

                if (connect)
                {
                    connection = await ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                else if (send)
                {
                    if (connection is not null && !await SendAsync(connection, activity, cancellationToken).ConfigureAwait(false))
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                        connection = null;
                        Disconnected("The Discord pipe broke");
                    }
                }
                else
                {
                    await WaitAsync(changed, wait, connection?.Closed, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "The Discord status stopped working");
        }
        finally
        {
            if (connection is not null)
            {
                await HangUpAsync(connection).ConfigureAwait(false);
            }
        }
    }

    private async Task<DiscordIpcConnection?> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = await DiscordIpcConnection.ConnectAsync(_options.PipeName, _options.ApplicationId, _logger, cancellationToken).ConfigureAwait(false);
            _connectFailures = 0;
            lock (_gate)
            {
                _coalescer.Reconnected(_desired);
            }

            SetConnected(true);
            _logger.LogInformation("Connected to Discord");
            return connection;
        }
        catch (DiscordRejectedException ex)
        {
            _nextAttempt = _time.GetUtcNow() + RejectedRetry;
            _logger.LogWarning("Discord refused the connection ({Code}): {Message}", ex.Code, ex.Message);
            return null;
        }
        catch (DiscordUnavailableException ex)
        {
            var delay = RetryDelay();
            _nextAttempt = _time.GetUtcNow() + delay;
            if (++_connectFailures == 1)
            {
                _logger.LogWarning("Discord status: {Reason} Trying again every {Seconds:0}–{Max:0} s while music plays", ex.Message, RetryMin.TotalSeconds, RetryMax.TotalSeconds);
            }
            else
            {
                _logger.LogDebug("Discord status: {Reason} Next try in {Delay}", ex.Message, delay);
            }

            return null;
        }
    }

    private async Task<bool> SendAsync(DiscordIpcConnection connection, string? activity, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SetActivityAsync(Environment.ProcessId, activity is null ? null : JsonNode.Parse(activity), cancellationToken).ConfigureAwait(false);
            if (activity is null)
            {
                _logger.LogDebug("Discord status cleared");
            }
            else
            {
                _logger.LogDebug("Discord status set");
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "Could not update the Discord status");
            return false;
        }
    }

    private void Disconnected(string reason)
    {
        _nextAttempt = _time.GetUtcNow() + RetryDelay();
        lock (_gate)
        {
            // Whatever shows now has to be sent again on the next connection.
            _coalescer.Reconnected(_desired);
        }

        SetConnected(false);
        _logger.LogInformation("{Reason}; Discord status will reconnect", reason);
    }

    private async Task HangUpAsync(DiscordIpcConnection connection)
    {
        try
        {
            // Discord drops the status when the pipe closes too, but clearing it first takes it away at once.
            using var timeout = new CancellationTokenSource(ClearTimeout);
            await connection.SetActivityAsync(Environment.ProcessId, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not clear the Discord status");
        }

        await connection.DisposeAsync().ConfigureAwait(false);
        SetConnected(false);
    }

    private async Task WaitAsync(Task changed, TimeSpan wait, Task? closed, CancellationToken cancellationToken)
    {
        if (wait != Timeout.InfiniteTimeSpan && wait <= TimeSpan.Zero)
        {
            return;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waits = new List<Task>(3) { changed };
        if (wait != Timeout.InfiniteTimeSpan)
        {
            waits.Add(Task.Delay(wait, _time, stop.Token));
        }

        if (closed is not null)
        {
            waits.Add(closed);
        }

        await Task.WhenAny(waits).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);
    }

    private void SetConnected(bool connected)
    {
        if (_connected == connected)
        {
            return;
        }

        _connected = connected;
        try
        {
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A Discord ConnectionChanged handler failed");
        }
    }

    private static TimeSpan RetryDelay() =>
        RetryMin + TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * (RetryMax - RetryMin).TotalMilliseconds);
}

public static class DiscordPresenceServiceCollectionExtensions
{
    /// <summary>The Discord "Listening to" status (off until the user turns it on).</summary>
    public static IServiceCollection AddDiscordPresence(this IServiceCollection services)
    {
        services.AddSingleton(_ => DiscordPresenceOptions.FromEnvironment());
        services.AddSingleton(sp => new DiscordPresenceService(
            sp.GetRequiredService<IPlayer>(),
            sp.GetRequiredService<IRadioNowPlaying>(),
            sp.GetRequiredService<ISettingsService>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<DiscordPresenceService>>(),
            sp.GetRequiredService<DiscordPresenceOptions>(),
            sp.GetService<IRadioTrackMatcher>()));
        services.AddSingleton<IDiscordPresence>(sp => sp.GetRequiredService<DiscordPresenceService>());
        services.AddHostedService(sp => sp.GetRequiredService<DiscordPresenceService>());
        return services;
    }
}
