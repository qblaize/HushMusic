using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// "Resume last session" (<see cref="AppSettings.ResumeLastSession"/>): saves the queue and the playback position to
/// <c>Cache\playback-session.json</c> and, on startup, restores them without playing. The player then shows the track
/// at the saved position and Play resolves it and starts there.
/// </summary>
/// <remarks>
/// Saved shortly after any queue or track change, every <see cref="SaveInterval"/> while playing, on pause and on shutdown.
/// The restore goes through <see cref="IQueueService.Restore"/> and <see cref="IPlayer.Seek"/>, so the shell hears the
/// usual TrackChanged/PositionChanged events whenever it subscribes.
/// </remarks>
public sealed class PlaybackSessionKeeper : IHostedService, IDisposable
{
    public const string FileName = "playback-session.json";
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(15);
    private const int FormatVersion = 1;
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IPlayer _player;
    private readonly IQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<PlaybackSessionKeeper> _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _gate = new();
    private ITimer? _saveTimer;
    private DateTimeOffset _lastSave;
    private bool _listening;

    public PlaybackSessionKeeper(
        IPlayer player,
        IQueueService queue,
        ISettingsService settings,
        IAppPaths paths,
        TimeProvider time,
        ILogger<PlaybackSessionKeeper> logger)
    {
        _player = player;
        _queue = queue;
        _settings = settings;
        _paths = paths;
        _time = time;
        _logger = logger;
    }

    private string FilePath => Path.Combine(_paths.Cache, FileName);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_settings.Current.ResumeLastSession)
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            DeleteFile();
        }

        lock (_gate)
        {
            _lastSave = _time.GetUtcNow();
            _saveTimer = _time.CreateTimer(_ => SaveInBackground(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _listening = true;
        }

        _queue.Changed += OnQueueChanged;
        _queue.CurrentChanged += OnQueueCurrentChanged;
        _player.TrackChanged += OnTrackChanged;
        _player.StatusChanged += OnStatusChanged;
        _player.PositionChanged += OnPositionChanged;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _listening = false;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        _queue.Changed -= OnQueueChanged;
        _queue.CurrentChanged -= OnQueueCurrentChanged;
        _player.TrackChanged -= OnTrackChanged;
        _player.StatusChanged -= OnStatusChanged;
        _player.PositionChanged -= OnPositionChanged;
        await SaveAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _listening = false;
            _saveTimer?.Dispose();
            _saveTimer = null;
        }
    }

    private void SaveInBackground() => _ = SaveAsync(CancellationToken.None);

    private void OnQueueChanged(object? sender, QueueChangedEventArgs e) => ScheduleSave(SaveDelay);

    private void OnQueueCurrentChanged(object? sender, QueueCurrentChangedEventArgs e) => ScheduleSave(SaveDelay);

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => ScheduleSave(SaveDelay);

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e)
    {
        if (e.Status is PlaybackStatus.Paused or PlaybackStatus.Ended)
        {
            ScheduleSave(TimeSpan.Zero);
        }
    }

    private void OnPositionChanged(object? sender, PositionChangedEventArgs e)
    {
        bool due;
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            due = now - _lastSave >= SaveInterval;
            if (due)
            {
                _lastSave = now;
            }
        }

        if (due)
        {
            ScheduleSave(TimeSpan.Zero);
        }
    }

    private void ScheduleSave(TimeSpan delay)
    {
        lock (_gate)
        {
            if (_listening)
            {
                _saveTimer?.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private async Task RestoreAsync(CancellationToken cancellationToken)
    {
        var session = await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        // Something already filled the queue (it never has at startup today; don't clobber it if it ever does).
        if (_queue.Items.Count > 0 || _player.CurrentTrack is not null)
        {
            _logger.LogDebug("Not restoring the last session: a queue is already loaded");
            return;
        }

        try
        {
            _queue.Restore(session.ToSnapshot());
            var current = _queue.Current;
            if (current is not null && session.Position > TimeSpan.Zero)
            {
                _player.Seek(session.Position);
            }

            _logger.LogInformation(
                "Restored the last session: {Count} tracks, current {VideoId} at {Position}",
                session.Items.Count,
                current?.Track.VideoId,
                session.Position);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not restore the last session");
        }
    }

    private async Task<PlaybackSession?> ReadAsync(CancellationToken cancellationToken)
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        PlaybackSession? session;
        try
        {
            await using var stream = File.OpenRead(path);
            session = await JsonSerializer.DeserializeAsync<PlaybackSession>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The saved playback session is unreadable; starting with an empty queue");
            DeleteFile();
            return null;
        }

        if (session is null || session.Version != FormatVersion || !session.IsValid())
        {
            _logger.LogWarning("The saved playback session is invalid; starting with an empty queue");
            DeleteFile();
            return null;
        }

        if (_time.GetUtcNow() - session.SavedAt > MaxAge)
        {
            _logger.LogInformation("The saved playback session is from {SavedAt}, older than {Days} days; not restoring it", session.SavedAt, MaxAge.TotalDays);
            DeleteFile();
            return null;
        }

        return session;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            var now = _time.GetUtcNow();
            lock (_gate)
            {
                _lastSave = now;
            }

            var snapshot = _queue.GetSnapshot();
            if (!_settings.Current.ResumeLastSession || snapshot.Items.Count == 0 || snapshot.CurrentIndex < 0)
            {
                DeleteFile();
                return;
            }

            var session = PlaybackSession.From(snapshot, CurrentPosition(snapshot), now);
            var path = FilePath;
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, session, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            await ReplaceAsync(temp, path, cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Saved the playback session: {Count} tracks, index {Index} at {Position}", session.Items.Count, session.CurrentIndex, session.Position);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not save the playback session");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // The rename fails while another process has the old file open for a moment (virus scanner, indexer): retry briefly
    // instead of losing this save.
    private static async Task ReplaceAsync(string temp, string path, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (attempt < 5 && ex is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(40 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // The player's position belongs to the queue's current item only when the player shows that same track.
    private TimeSpan CurrentPosition(QueueSnapshot snapshot)
    {
        var current = snapshot.Items[snapshot.CurrentIndex].Track;

        // A live station has no position: it restores paused and Play reconnects to the live stream.
        if (current.IsLiveRadio || _player.Status is PlaybackStatus.Ended || _player.CurrentTrack?.VideoId != current.VideoId)
        {
            return TimeSpan.Zero;
        }

        var position = _player.Position;
        return position > TimeSpan.Zero ? position : TimeSpan.Zero;
    }

    private void DeleteFile()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogDebug(ex, "Could not delete the saved playback session");
        }
    }

    /// <summary>On-disk format. Tracks are stored as the Core model.</summary>
    private sealed record PlaybackSession
    {
        public int Version { get; init; }

        public DateTimeOffset SavedAt { get; init; }

        public required IReadOnlyList<QueueItem> Items { get; init; }

        public int CurrentIndex { get; init; }

        public TimeSpan Position { get; init; }

        public QueueSource? Source { get; init; }

        public string? Continuation { get; init; }

        public RepeatMode RepeatMode { get; init; }

        /// <summary>Original order (item ids) while shuffled.</summary>
        public IReadOnlyList<Guid>? UnshuffledOrder { get; init; }

        public static PlaybackSession From(QueueSnapshot snapshot, TimeSpan position, DateTimeOffset now) => new()
        {
            Version = FormatVersion,
            SavedAt = now,
            Items = snapshot.Items,
            CurrentIndex = snapshot.CurrentIndex,
            Position = position,
            Source = snapshot.Source,
            Continuation = snapshot.Continuation,
            RepeatMode = snapshot.RepeatMode,
            UnshuffledOrder = snapshot.UnshuffledItems?.Select(i => i.Id).ToList(),
        };

        public bool IsValid() =>
            Items is { Count: > 0 }
            && Items.All(i => i is not null && i.Id != Guid.Empty && i.Track is { VideoId.Length: > 0 })
            && Position >= TimeSpan.Zero;

        public QueueSnapshot ToSnapshot()
        {
            IReadOnlyList<QueueItem>? unshuffled = null;
            if (UnshuffledOrder is { } order)
            {
                var byId = Items.GroupBy(i => i.Id).ToDictionary(g => g.Key, g => g.First());
                var mapped = order.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                unshuffled = mapped.Count == order.Count ? mapped : null;
            }

            return new QueueSnapshot(Items, CurrentIndex, Source, Continuation, RepeatMode, unshuffled);
        }
    }
}
