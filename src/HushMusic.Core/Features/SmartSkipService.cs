using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// <see cref="ISmartSkip"/>: counts quick skips from the player's events (<see cref="SkipTracker"/>) and keeps the counts
/// in <c>skipped-songs.json</c> under the app folder. Nothing runs between events: each skip or listen updates the counts
/// and the file is rewritten (a temp file that replaces the old one) in the background. While smart skip is off nothing
/// is counted. The file is read in the background at startup; skips counted before that are merged into it.
/// </summary>
public sealed partial class SmartSkipService : ISmartSkip, IHostedService
{
    public const string FileName = "skipped-songs.json";
    private const int FormatVersion = 1;
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(2);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IPlayer _player;
    private readonly IQueueService _queue;
    private readonly ISettingsService _settings;
    private readonly IAppPaths _paths;
    private readonly TimeProvider _time;
    private readonly ILogger<SmartSkipService> _logger;
    private readonly Lock _gate = new();
    private readonly SkipTracker _tracker = new();
    private readonly SkipCounts _counts = new();
    private readonly long _origin;

    // Guarded by _gate.
    private Task? _load;
    private bool _loaded;
    private bool _saveRequested;
    private Task _save = Task.CompletedTask;
    private bool _saving;
    private bool _enabled;

    // Swapped whole on every change, so the queue fillers read them without a lock.
    private volatile IReadOnlyList<SkippedSong> _oftenSkipped = [];
    private volatile IReadOnlySet<string> _leftOut = new HashSet<string>(StringComparer.Ordinal);

    public SmartSkipService(
        IPlayer player,
        IQueueService queue,
        ISettingsService settings,
        IAppPaths paths,
        TimeProvider time,
        ILogger<SmartSkipService> logger)
    {
        _player = player;
        _queue = queue;
        _settings = settings;
        _paths = paths;
        _time = time;
        _logger = logger;
        _origin = time.GetTimestamp();
    }

    public event EventHandler? Changed;

    public IReadOnlyList<SkippedSong> OftenSkipped => _oftenSkipped;

    private string FilePath => Path.Combine(_paths.Root, FileName);

    private bool IsEnabled => _settings.Current.SmartSkip;

    private TimeSpan Now => _time.GetElapsedTime(_origin);

    public bool IsLeftOut(string videoId) => IsEnabled && _leftOut.Contains(videoId);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _player.TrackChanged += OnTrackChanged;
        _player.TrackStarted += OnTrackStarted;
        _player.TrackCompleted += OnTrackCompleted;
        _player.StatusChanged += OnStatusChanged;
        _player.PlaybackFailed += OnPlaybackFailed;
        _settings.Changed += OnSettingsChanged;
        lock (_gate)
        {
            _enabled = IsEnabled;
        }

        _ = LoadAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _player.TrackChanged -= OnTrackChanged;
        _player.TrackStarted -= OnTrackStarted;
        _player.TrackCompleted -= OnTrackCompleted;
        _player.StatusChanged -= OnStatusChanged;
        _player.PlaybackFailed -= OnPlaybackFailed;
        _settings.Changed -= OnSettingsChanged;

        Task save;
        lock (_gate)
        {
            save = _save;
        }

        // The app is closing: give the last change a moment to reach the disk.
        await Task.WhenAny(save, Task.Delay(StopTimeout, _time, cancellationToken)).ConfigureAwait(false);
    }

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _load ??= Task.Run(ReadAsync, CancellationToken.None);
        }

        return load.WaitAsync(cancellationToken);
    }

    public async Task ForgiveAsync(string videoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        bool changed;
        lock (_gate)
        {
            if (!_counts.Contains(videoId))
            {
                return;
            }

            changed = _counts.Clear(videoId);
            PublishNoLock();
            RequestSaveNoLock();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Player events arrive in order on the player's event thread.

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        if (e.Track is not { } track)
        {
            return;
        }

        var duration = _player.Duration;
        var position = _player.Position;
        Apply(() => _tracker.Started(e.Item?.Id, track, position, duration > TimeSpan.Zero ? duration : null, Now));
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        Guid? current;
        lock (_gate)
        {
            current = _tracker.CurrentItemId;
        }

        // Only when there is a play to judge: the queue is copied to see which way the user went.
        var wentBack = current is { } from && e.Item is { } to && from != to.Id && IsBefore(to.Id, from);
        Apply(() => _tracker.MovedOn(e.Item?.Id, e.Track, wentBack, Now));
    }

    private void OnTrackCompleted(object? sender, TrackChangedEventArgs e) => Apply(() => _tracker.Completed(e.Item?.Id, e.Track));

    private void OnPlaybackFailed(object? sender, PlaybackErrorEventArgs e)
    {
        lock (_gate)
        {
            _tracker.Failed(e.Track);
        }
    }

    private void OnStatusChanged(object? sender, PlaybackStatusChangedEventArgs e)
    {
        var playing = e.Status == PlaybackStatus.Playing;
        var duration = _player.Duration;
        var videoId = playing && duration > TimeSpan.Zero ? _player.CurrentTrack?.VideoId : null;
        Apply(() =>
        {
            if (videoId is not null)
            {
                _tracker.DurationKnown(videoId, duration);
            }

            return _tracker.PlayingChanged(playing, Now);
        });
    }

    // Turning smart skip on or off changes what IsLeftOut answers.
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var enabled = IsEnabled;
        lock (_gate)
        {
            if (enabled == _enabled)
            {
                return;
            }

            _enabled = enabled;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsBefore(Guid item, Guid other)
    {
        var items = _queue.Items;
        int itemIndex = -1, otherIndex = -1;
        for (var i = 0; i < items.Count && (itemIndex < 0 || otherIndex < 0); i++)
        {
            if (items[i].Id == item)
            {
                itemIndex = i;
            }
            else if (items[i].Id == other)
            {
                otherIndex = i;
            }
        }

        return itemIndex >= 0 && otherIndex >= 0 && itemIndex < otherIndex;
    }

    private void Apply(Func<SkipOutcome> step)
    {
        var changed = false;
        lock (_gate)
        {
            var outcome = step();
            if (outcome.Track is not { } track || !IsEnabled)
            {
                return;
            }

            switch (outcome.Verdict)
            {
                case SkipVerdict.QuickSkip:
                    changed = _counts.AddSkip(track, _time.GetUtcNow());
                    LogQuickSkip(_logger, track.VideoId);
                    break;
                case SkipVerdict.Listened when _counts.Contains(track.VideoId):
                    changed = _counts.Clear(track.VideoId);
                    break;
                default:
                    return;
            }

            if (changed)
            {
                PublishNoLock();
            }

            RequestSaveNoLock();
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void PublishNoLock()
    {
        var often = _counts.OftenSkipped();
        _oftenSkipped = often;
        _leftOut = often.Select(s => s.VideoId).ToHashSet(StringComparer.Ordinal);
    }

    private async Task ReadAsync()
    {
        List<SkippedSong> saved = [];
        try
        {
            if (File.Exists(FilePath))
            {
                await using var stream = File.OpenRead(FilePath);
                var file = await JsonSerializer.DeserializeAsync<SkipsFile>(stream, JsonOptions).ConfigureAwait(false);
                saved = [.. file?.Songs?.Where(s => s is not null) ?? []];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _logger.LogWarning(ex, "The skipped songs file is unreadable; starting over");
        }

        lock (_gate)
        {
            _counts.Merge(saved);
            PublishNoLock();
            _loaded = true;
            if (_saveRequested)
            {
                RequestSaveNoLock();
            }
        }

        if (_oftenSkipped.Count > 0)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    // Changes that come in while a write runs are written once more right after it. Nothing is written before the file was
    // read, or the skips counted so far would replace it.
    private void RequestSaveNoLock()
    {
        _saveRequested = true;
        if (_loaded && !_saving)
        {
            _saving = true;
            _save = Task.Run(SaveAsync);
        }
    }

    private async Task SaveAsync()
    {
        while (true)
        {
            SkipsFile file;
            lock (_gate)
            {
                if (!_saveRequested)
                {
                    _saving = false;
                    return;
                }

                _saveRequested = false;
                file = new SkipsFile(FormatVersion, [.. _counts.All.OrderByDescending(s => s.LastSkipped)]);
            }

            try
            {
                var temp = FilePath + ".tmp";
                await using (var stream = File.Create(temp))
                {
                    await JsonSerializer.SerializeAsync(stream, file, JsonOptions).ConfigureAwait(false);
                }

                File.Move(temp, FilePath, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not save the skipped songs");
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Quick skip of {VideoId}")]
    private static partial void LogQuickSkip(ILogger logger, string videoId);

    private sealed record SkipsFile(int Version, IReadOnlyList<SkippedSong>? Songs);
}
