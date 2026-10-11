using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features.Stats;

/// <summary>
/// Announces badges as they are unlocked while the app runs: one quiet in-app notice, at most once per badge (the ones
/// already announced are kept in <c>history\achievements.json</c>). The whole log is read once, after the first play is
/// logged; from then on each new line is added to the running <see cref="AchievementTally"/>, and the badges are checked
/// at most once a minute.
/// </summary>
/// <remarks>
/// The first check after this file appears only takes note of what is already unlocked, so badges earned before (the log
/// goes back further than this feature) don't arrive as a burst of notices.
/// </remarks>
public sealed partial class AchievementWatcher : IHostedService, IDisposable
{
    internal const string FileName = "achievements.json";

    /// <summary>Badges are checked at most this often.</summary>
    internal static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(1);

    /// <summary>A play logs a couple of lines at once (one ending, the next starting): they are checked together.</summary>
    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);

    private readonly IPlayLog _log;
    private readonly INotificationService _notifications;
    private readonly TimeProvider _time;
    private readonly ILogger<AchievementWatcher> _logger;
    private readonly string _file;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly List<PlayRecord> _pending = [];
    private AchievementTally? _tally;
    private bool _rebuild = true;
    private bool _scheduled;
    private long? _lastCheck;
    private ITimer? _timer;

    public AchievementWatcher(IPlayLog log, INotificationService notifications, IAppPaths paths, TimeProvider time, ILogger<AchievementWatcher> logger)
        : this(log, notifications, Path.Combine(paths.Root, JsonLinesPlayLog.FolderName, FileName), time, logger)
    {
    }

    internal AchievementWatcher(IPlayLog log, INotificationService notifications, string file, TimeProvider time, ILogger<AchievementWatcher> logger)
    {
        _log = log;
        _notifications = notifications;
        _file = file;
        _time = time;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _timer = _time.CreateTimer(_ => _ = CheckAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _log.Changed += OnLogChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _log.Changed -= OnLogChanged;
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
        }
    }

    /// <summary>Checks the badges now: adds what was logged since the last check and announces what is newly unlocked.</summary>
    internal async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        await _checking.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<PlayRecord> pending;
            bool rebuild;
            lock (_gate)
            {
                pending = [.. _pending];
                _pending.Clear();
                rebuild = _rebuild || _tally is null;
                _rebuild = false;
                _scheduled = false;
                _lastCheck = _time.GetTimestamp();
            }

            var tally = _tally;
            if (rebuild || tally is null)
            {
                // Lines logged while this reads are in the file, the pending list, or both: adding one twice changes nothing.
                tally = new AchievementTally(_time.LocalTimeZone);
                foreach (var record in await _log.ReadAsync(null, cancellationToken).ConfigureAwait(false))
                {
                    tally.Add(record);
                }

                _tally = tally;
            }

            foreach (var record in pending)
            {
                tally.Add(record);
            }

            await AnnounceAsync([.. tally.UnlockedIds], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCheckFailed(_logger, ex);
        }
        finally
        {
            _checking.Release();
        }
    }

    private async Task AnnounceAsync(IReadOnlyCollection<string> unlocked, CancellationToken cancellationToken)
    {
        var shown = await ReadShownAsync(cancellationToken).ConfigureAwait(false);
        var fresh = shown is null ? [] : unlocked.Where(id => !shown.Contains(id)).ToList();
        if (shown is not null && fresh.Count == 0)
        {
            return;
        }

        List<string> remembered = shown is null ? [] : [.. shown];
        remembered.AddRange(unlocked.Where(id => shown?.Contains(id) != true));
        await WriteShownAsync(remembered, cancellationToken).ConfigureAwait(false);
        var titles = Achievements.Goals.Where(g => fresh.Contains(g.Id)).Select(g => g.Title).ToList();
        if (titles.Count > 0)
        {
            _notifications.Show(new AppNotification(
                NotificationSeverity.Success,
                titles.Count == 1 ? "Achievement unlocked" : $"{titles.Count} achievements unlocked",
                string.Join(", ", titles)));
        }
    }

    // Null when the file isn't there yet (the first check).
    private async Task<HashSet<string>?> ReadShownAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_file))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
            var file = await JsonSerializer.DeserializeAsync<ShownFile>(stream, JsonLinesPlayLog.JsonOptions, cancellationToken).ConfigureAwait(false);
            return new HashSet<string>(file?.Shown ?? [], StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Unreadable: start over, quietly (it is rewritten below).
            return null;
        }
    }

    private async Task WriteShownAsync(IReadOnlyList<string> shown, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var temporary = _file + ".tmp";
        await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(new ShownFile(shown), JsonLinesPlayLog.JsonOptions), cancellationToken).ConfigureAwait(false);
        File.Move(temporary, _file, overwrite: true);
    }

    // Arrives on the thread that wrote the line (or cleared the log).
    private void OnLogChanged(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (e is PlayLogChangedEventArgs { Appended: { } record })
            {
                _pending.Add(record);
            }
            else
            {
                // Cleared (or a log that doesn't say what changed): read it again.
                _rebuild = true;
                _pending.Clear();
            }

            if (_scheduled || _timer is null)
            {
                return;
            }

            _scheduled = true;
            var sinceLast = _lastCheck is { } last ? _time.GetElapsedTime(last) : TimeSpan.MaxValue;
            var wait = sinceLast >= CheckInterval ? SettleDelay : CheckInterval - sinceLast;
            _timer.Change(wait < SettleDelay ? SettleDelay : wait, Timeout.InfiniteTimeSpan);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not check the achievements")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);

    private sealed record ShownFile(IReadOnlyList<string> Shown);
}
