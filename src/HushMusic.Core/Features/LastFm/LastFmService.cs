using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features.LastFm;

/// <summary>
/// Last.fm desktop authentication and scrobbling. Sends "now playing" when a track starts and a scrobble once it has
/// been listened to long enough (<see cref="ScrobbleRules"/>). Scrobbles go through a persisted queue, so plays made
/// offline or during a Last.fm outage are sent later in batches.
/// </summary>
public sealed class LastFmService : ILastFmService, IHostedService, IDisposable
{
    internal const string SecretApiKey = "lastfm.apikey";
    internal const string SecretSharedSecret = "lastfm.secret";
    internal const string SecretSessionKey = "lastfm.session";
    internal const string SecretUserName = "lastfm.user";
    internal const string QueueFileName = "lastfm-queue.json";
    internal const int BatchSize = 50;
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(5);

    private readonly IPlayer _player;
    private readonly ISettingsService _settings;
    private readonly ISecretStore _secrets;
    private readonly INotificationService _notifications;
    private readonly ILogger<LastFmService> _logger;
    private readonly TimeProvider _time;
    private readonly LastFmClient _client;
    private readonly ScrobbleQueue _queue;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _authGate = new(1, 1);
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly CancellationToken _stoppingToken;

    private LastFmState _state;
    private LastFmCredentials? _credentials;
    private string? _sessionKey;
    private string? _userName;
    private string? _pendingToken;
    private ListenSession? _listen;
    private ITimer? _retryTimer;
    private int _flushRequested;

    public LastFmService(
        IPlayer player,
        ISettingsService settings,
        ISecretStore secrets,
        IAppPaths paths,
        INotificationService notifications,
        IHttpClientFactory httpClientFactory,
        ILogger<LastFmService> logger)
        : this(player, settings, secrets, paths, notifications, httpClientFactory, logger, TimeProvider.System)
    {
    }

    internal LastFmService(
        IPlayer player,
        ISettingsService settings,
        ISecretStore secrets,
        IAppPaths paths,
        INotificationService notifications,
        IHttpClientFactory httpClientFactory,
        ILogger<LastFmService> logger,
        TimeProvider time)
    {
        _player = player;
        _settings = settings;
        _secrets = secrets;
        _notifications = notifications;
        _logger = logger;
        _time = time;
        _client = new LastFmClient(httpClientFactory);
        _queue = new ScrobbleQueue(Path.Combine(paths.Cache, QueueFileName), logger);
        _stoppingToken = _stopping.Token;
    }

    public event EventHandler? StateChanged;

    public LastFmState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    public string? UserName
    {
        get
        {
            lock (_gate)
            {
                return _userName;
            }
        }
    }

    public int PendingScrobbles => _queue.Count;

    /// <summary>Completes when the stored session and queue have been loaded (never faults).</summary>
    internal Task Initialization { get; private set; } = Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _player.TrackChanged += OnTrackChanged;
        _player.TrackStarted += OnTrackStarted;
        _player.StatusChanged += OnStatusChanged;
        _player.PositionChanged += OnPositionChanged;
        _settings.Changed += OnSettingsChanged;
        Initialization = InitializeAsync(_stoppingToken);
        _retryTimer = _time.CreateTimer(_ => OnRetryTimer(), null, RetryInterval, RetryInterval);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _player.TrackChanged -= OnTrackChanged;
        _player.TrackStarted -= OnTrackStarted;
        _player.StatusChanged -= OnStatusChanged;
        _player.PositionChanged -= OnPositionChanged;
        _settings.Changed -= OnSettingsChanged;
        _retryTimer?.Dispose();
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _retryTimer?.Dispose();
        _stopping.Dispose();
    }

    public async Task<Uri> BeginConnectAsync(string apiKey, string sharedSecret, CancellationToken cancellationToken = default)
    {
        apiKey = (apiKey ?? string.Empty).Trim();
        sharedSecret = (sharedSecret ?? string.Empty).Trim();
        if (apiKey.Length == 0 || sharedSecret.Length == 0)
        {
            throw new ArgumentException("Enter both the API key and the shared secret.");
        }

        await Initialization.ConfigureAwait(false);
        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var credentials = new LastFmCredentials(apiKey, sharedSecret);
            var token = await _client.GetTokenAsync(credentials, cancellationToken).ConfigureAwait(false);

            await _secrets.WriteAsync(SecretApiKey, apiKey, cancellationToken).ConfigureAwait(false);
            await _secrets.WriteAsync(SecretSharedSecret, sharedSecret, cancellationToken).ConfigureAwait(false);
            await _secrets.WriteAsync(SecretSessionKey, null, cancellationToken).ConfigureAwait(false);
            await _secrets.WriteAsync(SecretUserName, null, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _credentials = credentials;
                _sessionKey = null;
                _userName = null;
                _pendingToken = token;
                _state = LastFmState.AwaitingApproval;
            }

            RaiseStateChanged();
            return LastFmClient.AuthorizationUri(apiKey, token);
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task<bool> CompleteConnectAsync(CancellationToken cancellationToken = default)
    {
        await Initialization.ConfigureAwait(false);
        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastFmCredentials? credentials;
            string? token;
            lock (_gate)
            {
                credentials = _credentials;
                token = _pendingToken;
            }

            if (credentials is null || token is null)
            {
                throw new InvalidOperationException("No Last.fm connection is in progress. Select Connect first.");
            }

            LastFmSession session;
            try
            {
                session = await _client.GetSessionAsync(credentials, token, cancellationToken).ConfigureAwait(false);
            }
            catch (LastFmException ex) when (ex.Code == LastFmException.TokenNotAuthorized)
            {
                return false;
            }
            catch (LastFmException ex) when (ex.Code is LastFmException.TokenExpired or 4)
            {
                // 15: the token is older than 60 minutes; 4: it was already used or is unknown. Either way, start over.
                lock (_gate)
                {
                    _pendingToken = null;
                    _state = LastFmState.Disconnected;
                }

                RaiseStateChanged();
                throw new LastFmException(ex.Code, "The Last.fm approval link expired. Select Connect to try again.", ex);
            }

            await _secrets.WriteAsync(SecretSessionKey, session.Key, cancellationToken).ConfigureAwait(false);
            await _secrets.WriteAsync(SecretUserName, session.UserName, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _sessionKey = session.Key;
                _userName = session.UserName;
                _pendingToken = null;
                _state = LastFmState.Connected;
            }

            _logger.LogInformation("Connected to Last.fm as {User}", session.UserName);
            RaiseStateChanged();
            _ = FlushAsync();
            return true;
        }
        finally
        {
            _authGate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await Initialization.ConfigureAwait(false);
        await _authGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_gate)
            {
                _credentials = null;
                _sessionKey = null;
                _userName = null;
                _pendingToken = null;
                _state = LastFmState.Disconnected;
            }

            // Pending plays belong to the account being disconnected; don't send them to the next one.
            _queue.Clear();
            RaiseStateChanged();
            foreach (var name in new[] { SecretSessionKey, SecretUserName, SecretApiKey, SecretSharedSecret })
            {
                await _secrets.WriteAsync(name, null, cancellationToken).ConfigureAwait(false);
            }

            await _queue.SaveAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _authGate.Release();
        }
    }

    /// <summary>Sends queued scrobbles in batches until the queue is empty or Last.fm is unreachable. Never throws.</summary>
    internal async Task FlushAsync()
    {
        Volatile.Write(ref _flushRequested, 1);
        while (true)
        {
            // One sender at a time; a request made while it runs is picked up by the loop below or after Release.
            if (!await _flushGate.WaitAsync(0).ConfigureAwait(false))
            {
                return;
            }

            try
            {
                while (Interlocked.Exchange(ref _flushRequested, 0) == 1)
                {
                    await SendQueuedAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                _flushGate.Release();
            }

            if (Volatile.Read(ref _flushRequested) == 0)
            {
                return;
            }
        }
    }

    private async Task SendQueuedAsync()
    {
        var cancellationToken = _stoppingToken;
        try
        {
            while (!cancellationToken.IsCancellationRequested && TryGetSession(out var credentials, out var sessionKey))
            {
                var batch = _queue.Peek(BatchSize);
                if (batch.Count == 0)
                {
                    break;
                }

                try
                {
                    var result = await _client.ScrobbleAsync(credentials, sessionKey, batch, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Scrobbled {Accepted} track(s) to Last.fm ({Ignored} ignored)", result.Accepted, result.Ignored);
                }
                catch (LastFmException ex) when (ex.Code == LastFmException.InvalidParameters)
                {
                    // Not retryable: drop the batch rather than block the queue forever.
                    _logger.LogWarning("Last.fm rejected {Count} scrobble(s) as invalid ({Message}); dropping them", batch.Count, ex.Message);
                }
                catch (LastFmException ex) when (!ex.IsTransient)
                {
                    await HandleRejectionAsync(ex).ConfigureAwait(false);
                    break;
                }
                catch (Exception ex) when (ex is LastFmException or HttpRequestException
                    || (ex is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    _logger.LogInformation("Last.fm is unreachable ({Message}); {Count} scrobble(s) will be retried later", ex.Message, _queue.Count);
                    break;
                }

                _queue.Remove(batch);
                await SaveQueueAsync().ConfigureAwait(false);
                RaiseStateChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sending scrobbles to Last.fm failed");
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _queue.LoadAsync(cancellationToken).ConfigureAwait(false);
            var apiKey = await _secrets.ReadAsync(SecretApiKey, cancellationToken).ConfigureAwait(false);
            var sharedSecret = await _secrets.ReadAsync(SecretSharedSecret, cancellationToken).ConfigureAwait(false);
            var sessionKey = await _secrets.ReadAsync(SecretSessionKey, cancellationToken).ConfigureAwait(false);
            var user = await _secrets.ReadAsync(SecretUserName, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(apiKey) && !string.IsNullOrEmpty(sharedSecret) && !string.IsNullOrEmpty(sessionKey) && !string.IsNullOrEmpty(user))
            {
                lock (_gate)
                {
                    _credentials = new LastFmCredentials(apiKey, sharedSecret);
                    _sessionKey = sessionKey;
                    _userName = user;
                    _state = LastFmState.Connected;
                }
            }

            if (State == LastFmState.Connected || _queue.Count > 0)
            {
                RaiseStateChanged();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the Last.fm connection");
            return;
        }

        _ = FlushAsync();
    }

    // Player events arrive on the player's event thread, in order.
    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        // Live radio is never scrobbled (the user's choice): its songs aren't the user's picks and have no reliable metadata.
        if (e.Track is not { IsLiveRadio: false } track)
        {
            return;
        }

        LastFmScrobble? finished;
        LastFmScrobble? nowPlaying;
        lock (_gate)
        {
            // Repeat-one replays the same item: close the previous play first.
            finished = _listen?.TryComplete();
            _listen = new ListenSession(track, _time, playing: true);
            nowPlaying = ScrobbleRules.ToScrobble(track, _listen.StartedAt, _listen.Duration);
        }

        Enqueue(finished);
        if (nowPlaying is not null && CanScrobble)
        {
            _ = SendNowPlayingAsync(nowPlaying);
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        LastFmScrobble? finished;
        lock (_gate)
        {
            finished = _listen?.TryComplete();
            _listen = null;
        }

        Enqueue(finished);
    }

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e)
    {
        LastFmScrobble? finished;
        lock (_gate)
        {
            if (_listen is null)
            {
                return;
            }

            // Count up to now before the state flips, so the last stretch of playing time isn't lost.
            finished = _listen.TryComplete();
            _listen.SetPlaying(e.Status == PlaybackStatus.Playing);
        }

        Enqueue(finished);
    }

    private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        LastFmScrobble? finished;
        lock (_gate)
        {
            if (_listen is null)
            {
                return;
            }

            _listen.ReportDuration(e.Duration);
            finished = _listen.TryComplete();
        }

        Enqueue(finished);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (CanScrobble && _queue.Count > 0)
        {
            _ = FlushAsync();
        }
    }

    private void OnRetryTimer()
    {
        if (CanScrobble && _queue.Count > 0)
        {
            _ = FlushAsync();
        }
    }

    private bool CanScrobble => State == LastFmState.Connected && _settings.Current.LastFmScrobbling;

    private void Enqueue(LastFmScrobble? scrobble)
    {
        // Plays are only recorded for a connected account with scrobbling on; offline plays are queued, not dropped.
        if (scrobble is null || !CanScrobble)
        {
            return;
        }

        _queue.Enqueue(scrobble);
        RaiseStateChanged();
        _ = SaveThenFlushAsync();
    }

    private async Task SaveThenFlushAsync()
    {
        await SaveQueueAsync().ConfigureAwait(false);
        await FlushAsync().ConfigureAwait(false);
    }

    private async Task SaveQueueAsync()
    {
        try
        {
            await _queue.SaveAsync(_stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save the Last.fm scrobble queue");
        }
    }

    private async Task SendNowPlayingAsync(LastFmScrobble track)
    {
        if (!TryGetSession(out var credentials, out var sessionKey))
        {
            return;
        }

        try
        {
            await _client.UpdateNowPlayingAsync(credentials, sessionKey, track, _stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (LastFmException ex) when (!ex.IsTransient && ex.Code != LastFmException.InvalidParameters)
        {
            await HandleRejectionAsync(ex).ConfigureAwait(false);
            return;
        }
        catch (Exception ex)
        {
            // Now playing is best effort; the scrobble itself is queued and retried.
            _logger.LogDebug("Last.fm now playing failed: {Message}", ex.Message);
            return;
        }

        // Last.fm is reachable again: a good moment to send anything left over.
        if (_queue.Count > 0)
        {
            _ = FlushAsync();
        }
    }

    /// <summary>Session or API key no longer accepted: disconnect (keeping the queue) and tell the user once.</summary>
    private async Task HandleRejectionAsync(LastFmException ex)
    {
        var message = ex.Code switch
        {
            LastFmException.InvalidSessionKey => "Last.fm no longer accepts this app's session (access was revoked or expired). Connect again in Settings.",
            LastFmException.InvalidApiKey or LastFmException.SuspendedApiKey => "Last.fm rejected your API key. Check it on last.fm and connect again in Settings.",
            _ => null,
        };

        if (message is null)
        {
            _logger.LogWarning("Last.fm refused a request: {Code} {Message}", ex.Code, ex.Message);
            return;
        }

        bool wasConnected;
        lock (_gate)
        {
            wasConnected = _state == LastFmState.Connected;
            _sessionKey = null;
            _userName = null;
            _state = LastFmState.Disconnected;
        }

        if (!wasConnected)
        {
            return;
        }

        _logger.LogWarning("Last.fm disconnected: {Code} {Message}", ex.Code, ex.Message);
        RaiseStateChanged();
        _notifications.Show(new AppNotification(NotificationSeverity.Warning, "Last.fm disconnected", message));
        try
        {
            await _secrets.WriteAsync(SecretSessionKey, null).ConfigureAwait(false);
            await _secrets.WriteAsync(SecretUserName, null).ConfigureAwait(false);
        }
        catch (Exception writeError)
        {
            _logger.LogWarning(writeError, "Could not delete the stored Last.fm session");
        }
    }

    private bool TryGetSession([NotNullWhen(true)] out LastFmCredentials? credentials, [NotNullWhen(true)] out string? sessionKey)
    {
        lock (_gate)
        {
            if (_state == LastFmState.Connected && _credentials is not null && _sessionKey is not null)
            {
                credentials = _credentials;
                sessionKey = _sessionKey;
                return true;
            }
        }

        credentials = null;
        sessionKey = null;
        return false;
    }

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "A Last.fm StateChanged handler failed");
        }
    }
}
