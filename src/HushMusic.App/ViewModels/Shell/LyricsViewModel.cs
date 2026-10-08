using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>
/// Lyrics for the current track, synced line by line when YouTube has timed lyrics. Loads only while the lyrics tab is
/// visible; a track change cancels the request in flight. Two calls: "next" for the lyrics browse id, then "browse"
/// (as the mobile client, for timestamps) for the text.
/// </summary>
public sealed partial class LyricsViewModel : ObservableObject
{
    private const int CacheSize = 30;
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(80);

    // A line lights up a moment early: position reports lag a little and the eye needs time to get there.
    private static readonly TimeSpan Lead = TimeSpan.FromMilliseconds(200);

    // The player reports ~4 times a second; between reports the position is extrapolated, but never far.
    private static readonly TimeSpan MaxExtrapolation = TimeSpan.FromSeconds(1.5);

    private readonly IPlayer _player;
    private readonly IWatchApi _watch;
    private readonly TrackBrowseIds _browseIds;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<LyricsViewModel> _logger;
    private readonly Dictionary<string, Lyrics?> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _cacheOrder = new();
    private readonly Lock _clockGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _reportedPosition;
    private TimeSpan _reportedAt;
    private volatile bool _isPlaying;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _cts;
    private Track? _track;
    private string? _shownVideoId;
    private bool _isActive;

    public LyricsViewModel(
        IPlayer player,
        IWatchApi watch,
        TrackBrowseIds browseIds,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<LyricsViewModel> logger)
    {
        _player = player;
        _watch = watch;
        _browseIds = browseIds;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        player.TrackChanged += (_, e) => _dispatcher.Run(() => OnTrackChanged(e.Track));
        player.PositionChanged += (_, e) => Report(e.Position);
        player.StatusChanged += (_, e) => _isPlaying = e.Status == PlaybackStatus.Playing;
        _track = player.CurrentTrack;
        _isPlaying = player.Status == PlaybackStatus.Playing;
        Report(player.Position);
        ShowEmptyState();
    }

    /// <summary>New lyrics replaced the old ones (the view scrolls back to the top).</summary>
    public event EventHandler? LyricsReplaced;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Unsynced lyrics; null when there are none or they are synced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlain))]
    public partial string? Text { get; set; }

    /// <summary>Synced lines; empty when the lyrics have no timestamps.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynced))]
    public partial IReadOnlyList<LyricLineViewModel> Lines { get; set; } = [];

    /// <summary>Index into <see cref="Lines"/> of the line being sung; -1 before the first one.</summary>
    [ObservableProperty]
    public partial int CurrentIndex { get; set; } = -1;

    /// <summary>"Source: LyricFind" and the like.</summary>
    [ObservableProperty]
    public partial string? Source { get; set; }

    /// <summary>Empty-state text ("No lyrics for this song."); null while lyrics, loading or an error are shown.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool IsSynced => Lines.Count > 0;

    public bool IsPlain => !string.IsNullOrWhiteSpace(Text);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The lyrics tab became visible (true) or hidden (false).</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            EnsureLoaded();
        }

        UpdateTimer();
    }

    /// <summary>A synced line was clicked: play from its start.</summary>
    public void SeekTo(LyricLineViewModel line)
    {
        try
        {
            _player.Seek(line.Start);
            Report(line.Start);
            Tick();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not seek", ex);
        }
    }

    [RelayCommand]
    private void Retry()
    {
        _shownVideoId = null;
        EnsureLoaded();
    }

    private void Report(TimeSpan position)
    {
        lock (_clockGate)
        {
            _reportedPosition = position;
            _reportedAt = _clock.Elapsed;
        }
    }

    private TimeSpan EstimatedPosition()
    {
        lock (_clockGate)
        {
            if (!_isPlaying)
            {
                return _reportedPosition;
            }

            var elapsed = _clock.Elapsed - _reportedAt;
            return _reportedPosition + (elapsed < MaxExtrapolation ? elapsed : MaxExtrapolation);
        }
    }

    private void OnTrackChanged(Track? track)
    {
        if (track?.VideoId == _track?.VideoId && track is not null)
        {
            return;
        }

        _track = track;
        _cts?.Cancel();
        _shownVideoId = null;
        Report(TimeSpan.Zero);
        if (_isActive)
        {
            EnsureLoaded();
        }
        else
        {
            // Hidden: drop the old lyrics now so the tab never flashes the previous song's text.
            Clear();
        }
    }

    private void EnsureLoaded()
    {
        var track = _track;
        if (track is null)
        {
            _cts?.Cancel();
            ShowEmptyState();
            return;
        }

        if (_shownVideoId == track.VideoId)
        {
            return;
        }

        if (track.IsLiveRadio)
        {
            // Live radio is not on YouTube: there is nothing to ask for.
            _cts?.Cancel();
            Clear();
            IsLoading = false;
            _shownVideoId = track.VideoId;
            Message = "Lyrics aren't available for live radio.";
            return;
        }

        _ = LoadAsync(track);
    }

    private async Task LoadAsync(Track track)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Clear();
        IsLoading = true;
        try
        {
            if (!_cache.TryGetValue(track.VideoId, out var lyrics))
            {
                var watch = await _browseIds.GetAsync(track.VideoId, cts.Token);
                lyrics = watch.LyricsBrowseId is { Length: > 0 } browseId
                    ? await _watch.GetLyricsAsync(browseId, timestamps: true, cts.Token)
                    : null;
                Remember(track.VideoId, lyrics);
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _shownVideoId = track.VideoId;
            Show(lyrics);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load lyrics for {VideoId}", track.VideoId);
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
            }
        }
    }

    private void Show(Lyrics? lyrics)
    {
        if (lyrics?.TimedLines is { Count: > 0 } timed)
        {
            Lines = [.. timed.Select((line, i) => new LyricLineViewModel(i, line.Start, line.Text))];
            Source = lyrics.Source;
            Tick();
        }
        else if (!string.IsNullOrWhiteSpace(lyrics?.Text))
        {
            Text = lyrics.Text.Trim();
            Source = lyrics.Source;
        }
        else
        {
            Message = "No lyrics for this song.";
        }

        LyricsReplaced?.Invoke(this, EventArgs.Empty);
        UpdateTimer();
    }

    private void Clear()
    {
        Lines = [];
        CurrentIndex = -1;
        Text = null;
        Source = null;
        Message = null;
        ErrorMessage = null;
        LyricsReplaced?.Invoke(this, EventArgs.Empty);
        UpdateTimer();
    }

    private void ShowEmptyState()
    {
        Clear();
        IsLoading = false;
        Message = "Play a song to see its lyrics here.";
    }

    private void Remember(string videoId, Lyrics? lyrics)
    {
        if (_cache.TryAdd(videoId, lyrics))
        {
            _cacheOrder.Enqueue(videoId);
            while (_cacheOrder.Count > CacheSize)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }
        }
    }

    // The highlight is driven by a short UI timer only while synced lines are on screen.
    private void UpdateTimer()
    {
        var run = _isActive && IsSynced;
        if (!run)
        {
            _timer?.Stop();
            return;
        }

        if (_timer is null)
        {
            if (DispatcherQueue.GetForCurrentThread() is not { } queue)
            {
                return;
            }

            _timer = queue.CreateTimer();
            _timer.Interval = TickInterval;
            _timer.IsRepeating = true;
            _timer.Tick += (_, _) => Tick();
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    private void Tick()
    {
        var lines = Lines;
        if (lines.Count == 0)
        {
            return;
        }

        var position = EstimatedPosition() + Lead;

        // Last line that has started (lines are in time order).
        int low = 0, high = lines.Count - 1, index = -1;
        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (lines[mid].Start <= position)
            {
                index = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (index == CurrentIndex)
        {
            return;
        }

        if (CurrentIndex >= 0 && CurrentIndex < lines.Count)
        {
            lines[CurrentIndex].IsCurrent = false;
        }

        if (index >= 0)
        {
            lines[index].IsCurrent = true;
        }

        CurrentIndex = index;
    }
}

/// <summary>One synced lyric line.</summary>
public sealed partial class LyricLineViewModel(int index, TimeSpan start, string text) : ObservableObject
{
    public int Index { get; } = index;

    public TimeSpan Start { get; } = start;

    /// <summary>Instrumental breaks come through as empty lines.</summary>
    public string Text { get; } = string.IsNullOrWhiteSpace(text) ? "♪" : text.Trim();

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }
}
