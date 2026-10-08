using System.Net;
using System.Text;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>One form-encoded Last.fm call as the fake server received it.</summary>
internal sealed record LastFmCall(IReadOnlyDictionary<string, string> Form)
{
    public string Method => Form["method"];
}

/// <summary>Fake ws.audioscrobbler.com: records every call and answers with <see cref="Respond"/>.</summary>
internal sealed class LastFmFakeServer : HttpMessageHandler
{
    private readonly List<LastFmCall> _calls = [];

    /// <summary>Default: an empty success body.</summary>
    public Func<LastFmCall, HttpResponseMessage> Respond { get; set; } = _ => Json("{}");

    public IReadOnlyList<LastFmCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public IReadOnlyList<LastFmCall> CallsTo(string method) => [.. Calls.Where(c => c.Method == method)];

    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Error(int code, string message = "error", HttpStatusCode status = HttpStatusCode.OK) =>
        Json($$"""{"error":{{code}},"message":"{{message}}"}""", status);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://ws.audioscrobbler.com/2.0/", request.RequestUri?.ToString());
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        var call = new LastFmCall(ParseForm(body));
        lock (_calls)
        {
            _calls.Add(call);
        }

        return Respond(call);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            form[Decode(parts[0])] = parts.Length > 1 ? Decode(parts[1]) : string.Empty;
        }

        return form;
    }

    private static string Decode(string value) => Uri.UnescapeDataString(value.Replace('+', ' '));
}

internal sealed class LastFmFakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

internal sealed class LastFmFakePlayer : IPlayer
{
    public PlaybackStatus Status { get; private set; }

    public Track? CurrentTrack { get; private set; }

    public TimeSpan Position => TimeSpan.Zero;

    public TimeSpan Duration => TimeSpan.Zero;

    public double Volume { get; set; }

    public bool IsMuted { get; set; }

    public bool IsShuffleEnabled { get; set; }

    public RepeatMode RepeatMode { get; set; }

    public event EventHandler<PlaybackStatusChangedEventArgs>? StatusChanged;

    public event EventHandler<TrackChangedEventArgs>? TrackChanged;

    public event EventHandler<TrackChangedEventArgs>? TrackStarted;

    public event EventHandler<TrackChangedEventArgs>? TrackCompleted { add { } remove { } }

    public event EventHandler<PositionChangedEventArgs>? PositionChanged;

    public event EventHandler<PlaybackErrorEventArgs>? PlaybackFailed { add { } remove { } }

    /// <summary>What MediaPlayerService does for a new track: TrackChanged, Loading, Playing, then TrackStarted.</summary>
    public void Start(Track track)
    {
        CurrentTrack = track;
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(track, null));
        SetStatus(PlaybackStatus.Loading);
        SetStatus(PlaybackStatus.Playing);
        TrackStarted?.Invoke(this, new TrackChangedEventArgs(track, null));
    }

    public void SetStatus(PlaybackStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this, new PlaybackStatusChangedEventArgs(status));
    }

    public void Tick(TimeSpan position, TimeSpan duration) =>
        PositionChanged?.Invoke(this, new PositionChangedEventArgs(position, duration));

    public void ChangeTrack(Track? track)
    {
        CurrentTrack = track;
        TrackChanged?.Invoke(this, new TrackChangedEventArgs(track, null));
    }

    public Task PlayAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PlayQueueIndexAsync(int index, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Pause() => SetStatus(PlaybackStatus.Paused);

    public Task TogglePlayPauseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Seek(TimeSpan position)
    {
    }

    public Task NextAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PreviousAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void Stop()
    {
    }
}

internal sealed class LastFmFakeSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        update(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

internal sealed class LastFmFakeSecretStore : ISecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        lock (Values)
        {
            return Task.FromResult(Values.GetValueOrDefault(name));
        }
    }

    public Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default)
    {
        lock (Values)
        {
            if (value is null)
            {
                Values.Remove(name);
            }
            else
            {
                Values[name] = value;
            }
        }

        return Task.CompletedTask;
    }
}

internal sealed class LastFmFakeNotifications : INotificationService
{
    private readonly List<AppNotification> _shown = [];

    public event EventHandler<AppNotification>? Raised;

    public IReadOnlyList<AppNotification> Shown
    {
        get
        {
            lock (_shown)
            {
                return [.. _shown];
            }
        }
    }

    public void Show(AppNotification notification)
    {
        lock (_shown)
        {
            _shown.Add(notification);
        }

        Raised?.Invoke(this, notification);
    }

    public void ShowError(string title, Exception exception) =>
        Show(new AppNotification(NotificationSeverity.Error, title, exception.Message, exception));

    public void ShowInfo(string title, string message) =>
        Show(new AppNotification(NotificationSeverity.Informational, title, message));
}

/// <summary>A clock the test moves by hand. Timers only fire through <see cref="FireTimers"/>.</summary>
internal sealed class LastFmManualTime : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_timers)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public void Advance(TimeSpan by)
    {
        lock (_timers)
        {
            _now += by;
        }
    }

    public void FireTimers()
    {
        ManualTimer[] timers;
        lock (_timers)
        {
            timers = [.. _timers];
        }

        foreach (var timer in timers)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
    {
        private bool _disposed;

        public void Fire()
        {
            if (!_disposed)
            {
                callback(state);
            }
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
