using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Lyrics;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>
/// Lyrics for the current track from <see cref="ILyricsService"/> (the user's source order), shown line by line and,
/// where the timing allows, word by word. Loads only while the lyrics tab is visible; a track change cancels the
/// request in flight.
/// </summary>
/// <remarks>
/// The view animates the words itself (on the compositor) from <see cref="GetPosition"/>; this keeps the clock and says
/// which line is current. It wakes up only at line changes (a one-shot timer set for the next one), never on a fixed tick.
/// </remarks>
public sealed partial class LyricsViewModel : ObservableObject
{
    // A line becomes current a moment early: position reports lag a little, and the eye needs time to get there.
    private static readonly TimeSpan LineLead = TimeSpan.FromMilliseconds(200);

    // The player reports ~4 times a second; between reports the position is extrapolated, but never far.
    private static readonly TimeSpan MaxExtrapolation = TimeSpan.FromSeconds(1.5);

    // A reported position this far from the extrapolated one is a seek (or a stall): the view re-syncs its animations.
    private static readonly TimeSpan JumpThreshold = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan MinTimerInterval = TimeSpan.FromMilliseconds(15);

    private const int ChosenSourceMemory = 50;

    private readonly IPlayer _player;
    private readonly ILyricsService _lyrics;
    private readonly ISettingsService _settings;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<LyricsViewModel> _logger;
    private readonly Dictionary<string, string> _chosenSource = new(StringComparer.Ordinal);
    private readonly Queue<string> _chosenOrder = new();
    private readonly Lock _clockGate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _reportedPosition;
    private TimeSpan _reportedAt;
    private volatile bool _isPlaying;
    private DispatcherQueueTimer? _timer;
    private CancellationTokenSource? _cts;
    private Track? _track;
    private string? _shownVideoId;
    private LyricsDocument? _document;
    private bool _estimateWords;
    private bool _isActive;

    public LyricsViewModel(
        IPlayer player,
        ILyricsService lyrics,
        ISettingsService settings,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        ILogger<LyricsViewModel> logger)
    {
        _player = player;
        _lyrics = lyrics;
        _settings = settings;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        _estimateWords = settings.Current.EstimateWordTiming;
        player.TrackChanged += (_, e) => _dispatcher.Run(() => OnTrackChanged(e.Track));
        player.PositionChanged += (_, e) => OnPositionChanged(e.Position);
        player.StatusChanged += (_, e) => OnStatusChanged(e.Status);
        lyrics.SourceOrderChanged += (_, _) => _dispatcher.Run(OnSourceOrderChanged);
        settings.Changed += (_, _) => _dispatcher.Run(OnSettingsChanged);
        _track = player.CurrentTrack;
        _isPlaying = player.Status == PlaybackStatus.Playing;
        Report(player.Position);
        ShowEmptyState();
    }

    /// <summary>New lyrics replaced the old ones (the view rebuilds and scrolls back to the top).</summary>
    public event EventHandler? LyricsReplaced;

    /// <summary>
    /// The clock jumped (a seek), or playback paused or resumed: animations running from the old position are wrong now.
    /// Raised on the UI thread.
    /// </summary>
    public event EventHandler? TimingChanged;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    /// <summary>Unsynced lyrics; null when there are none or they are synced.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlain))]
    public partial string? Text { get; set; }

    /// <summary>Synced lines and instrumental breaks, in time order; empty when the lyrics aren't timed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynced))]
    public partial IReadOnlyList<LyricLineViewModel> Lines { get; set; } = [];

    /// <summary>Index into <see cref="Lines"/> of the line (or break) being sung; -1 before the first one.</summary>
    [ObservableProperty]
    public partial int CurrentIndex { get; set; } = -1;

    /// <summary>The source shown, e.g. "LRCLIB"; null while nothing is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSource), nameof(HasSourceLink), nameof(HasPlainSourceName))]
    public partial string? SourceName { get; set; }

    /// <summary>A page about these lyrics at the source, when it has one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceLink), nameof(HasPlainSourceName))]
    public partial Uri? SourceUri { get; set; }

    /// <summary>"Written by …", when the source names the songwriters.</summary>
    [ObservableProperty]
    public partial string? Credits { get; set; }

    /// <summary>The source entry shown (a key of <see cref="ILyricsService.Sources"/>).</summary>
    [ObservableProperty]
    public partial string? SourceKey { get; set; }

    /// <summary>Source entries known to have lyrics for this song, in the user's order (the "Switch source" menu).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LyricsSourceInfo> AvailableSources { get; set; } = [];

    /// <summary>The sources not tried yet for this song are being asked (<see cref="FindOtherSourcesAsync"/>).</summary>
    [ObservableProperty]
    public partial bool IsFindingSources { get; set; }

    /// <summary>Every enabled source was asked about this song (the menu's list is complete).</summary>
    public bool HasSearchedSources { get; private set; }

    /// <summary>Empty-state text ("No lyrics for this song."); null while lyrics, loading or an error are shown.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool IsSynced => Lines.Count > 0;

    public bool IsPlain => !string.IsNullOrWhiteSpace(Text);

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasSource => !string.IsNullOrEmpty(SourceName);

    public bool HasSourceLink => HasSource && SourceUri is not null;

    public bool HasPlainSourceName => HasSource && SourceUri is null;

    /// <summary>True while the song plays (the clock runs).</summary>
    public bool IsPlaying => _isPlaying;

    /// <summary>How precisely a source is timed, as Better Lyrics labels it: "Syllable", "Word", "Line" or "Unsynced".</summary>
    public static string SyncLabel(LyricsSyncLevel sync) => sync switch
    {
        LyricsSyncLevel.Syllable => "Syllable",
        LyricsSyncLevel.Word => "Word",
        LyricsSyncLevel.Line => "Line",
        _ => "Unsynced",
    };

    /// <summary>The playback position now, extrapolated from the last report. Thread-safe.</summary>
    public TimeSpan GetPosition()
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

    /// <summary>The lyrics tab became visible (true) or hidden (false).</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            EnsureLoaded();
            Tick();
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
            TimingChanged?.Invoke(this, EventArgs.Empty);
            Tick();
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not seek", ex);
        }
    }

    /// <summary>
    /// Asks the enabled sources that weren't needed for this song yet (the lyrics come from the first that has them), so
    /// that "Switch source" can list every source with lyrics. Only when the menu is opened: it costs a request each.
    /// </summary>
    public async Task FindOtherSourcesAsync()
    {
        if (_track is not { } track || _document is null || IsFindingSources || HasSearchedSources)
        {
            return;
        }

        var token = _cts?.Token ?? CancellationToken.None;
        IsFindingSources = true;
        try
        {
            var known = AvailableSources.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var source in _lyrics.GetSourceOrder().Where(s => s.Enabled && !known.Contains(s.Key)))
            {
                try
                {
                    // Sources of one provider share a request: the second one is answered from the cache.
                    await _lyrics.GetLyricsAsync(track, source.Key, token);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(ex, "Lyrics source {Source} failed for {VideoId}", source.Key, track.VideoId);
                }
            }

            if (await ShowAvailableSourcesAsync(track, token))
            {
                HasSearchedSources = true;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsFindingSources = false;
        }
    }

    /// <summary>Shows this song's lyrics from another source (remembered for the song while the app runs).</summary>
    [RelayCommand]
    private void SwitchSource(string? key)
    {
        if (_track is not { } track || string.IsNullOrEmpty(key) || key == SourceKey)
        {
            return;
        }

        RememberChoice(track.VideoId, key);
        _shownVideoId = null;
        EnsureLoaded();
    }

    [RelayCommand]
    private void Retry()
    {
        _shownVideoId = null;
        EnsureLoaded();
    }

    // Player events arrive on background threads.
    private void OnPositionChanged(TimeSpan position)
    {
        var jumped = Math.Abs((position - GetPosition()).Ticks) > JumpThreshold.Ticks;
        Report(position);

        // Playing, the timer follows the clock; paused, only a seek moves it.
        if ((jumped || !_isPlaying) && _isActive)
        {
            _dispatcher.Run(() =>
            {
                if (jumped)
                {
                    TimingChanged?.Invoke(this, EventArgs.Empty);
                }

                Tick();
            });
        }
    }

    private void OnStatusChanged(PlaybackStatus status)
    {
        var playing = status == PlaybackStatus.Playing;
        if (playing == _isPlaying)
        {
            return;
        }

        // The clock restarts from the last report, which is where playback stopped or resumes.
        lock (_clockGate)
        {
            if (!playing)
            {
                var elapsed = _clock.Elapsed - _reportedAt;
                _reportedPosition += elapsed < MaxExtrapolation ? elapsed : MaxExtrapolation;
            }

            _reportedAt = _clock.Elapsed;
            _isPlaying = playing;
        }

        _dispatcher.Run(() =>
        {
            OnPropertyChanged(nameof(IsPlaying));
            TimingChanged?.Invoke(this, EventArgs.Empty);
            UpdateTimer();
            Tick();
        });
    }

    private void Report(TimeSpan position)
    {
        lock (_clockGate)
        {
            _reportedPosition = position;
            _reportedAt = _clock.Elapsed;
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

    // The order or a source's switch changed in Settings: the song may now get its lyrics from another source.
    private void OnSourceOrderChanged()
    {
        if (_track is { } track && !_chosenSource.ContainsKey(track.VideoId))
        {
            _shownVideoId = null;
            if (_isActive)
            {
                EnsureLoaded();
            }
        }
    }

    private void OnSettingsChanged()
    {
        var estimate = _settings.Current.EstimateWordTiming;
        if (estimate == _estimateWords)
        {
            return;
        }

        _estimateWords = estimate;
        if (_document is { } document && IsSynced)
        {
            Show(document);
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

        _ = LoadAsync(track, _chosenSource.GetValueOrDefault(track.VideoId));
    }

    private async Task LoadAsync(Track track, string? sourceKey)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Clear();
        IsLoading = true;
        try
        {
            var document = sourceKey is null ? null : await _lyrics.GetLyricsAsync(track, sourceKey, cts.Token);
            if (document is null)
            {
                // No choice for this song, or the chosen source has nothing now: the user's order decides.
                _chosenSource.Remove(track.VideoId);
                document = await _lyrics.GetLyricsAsync(track, cts.Token);
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            _shownVideoId = track.VideoId;
            Show(document);
            if (document is not null)
            {
                await ShowAvailableSourcesAsync(track, cts.Token);
            }
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

    private void Show(LyricsDocument? document)
    {
        _document = document;
        Lines = [];
        CurrentIndex = -1;
        Text = null;
        Message = null;

        if (document is null || document.Lines.Count == 0)
        {
            Message = document?.IsInstrumental == true ? "This song is instrumental." : "No lyrics for this song.";
            SourceName = null;
            SourceUri = null;
            Credits = null;
        }
        else
        {
            SourceName = document.SourceName;
            SourceUri = Uri.TryCreate(document.SourceUrl, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri : null;
            SourceKey = document.SourceKey;
            Credits = string.IsNullOrWhiteSpace(document.Writers) ? null : $"Written by {document.Writers.Trim()}";
            if (document.Sync == LyricsSyncLevel.Unsynced)
            {
                Text = string.Join('\n', document.Lines.Select(l => l.Text.TrimEnd())).Trim();
                if (Text.Length == 0)
                {
                    Text = null;
                    Message = "No lyrics for this song.";
                }
            }
            else
            {
                Lines = LyricsTimeline.Build(document, _estimateWords);
                if (Lines.Count == 0)
                {
                    Message = "No lyrics for this song.";
                }
            }
        }

        LyricsReplaced?.Invoke(this, EventArgs.Empty);
        Tick();
        UpdateTimer();
    }

    private void Clear()
    {
        _document = null;
        HasSearchedSources = false;
        Lines = [];
        CurrentIndex = -1;
        Text = null;
        SourceName = null;
        SourceUri = null;
        SourceKey = null;
        Credits = null;
        AvailableSources = [];
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

    // The "Switch source" list; failing to read it doesn't hide the lyrics on screen. True when it was updated.
    private async Task<bool> ShowAvailableSourcesAsync(Track track, CancellationToken cancellationToken)
    {
        IReadOnlyList<LyricsSourceInfo> available;
        try
        {
            available = await _lyrics.GetAvailableSourcesAsync(track, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not list the lyrics sources for {VideoId}", track.VideoId);
            return false;
        }

        if (cancellationToken.IsCancellationRequested || _track?.VideoId != track.VideoId)
        {
            return false;
        }

        AvailableSources = WithCurrent(available);
        return true;
    }

    // The menu always lists the source on screen, even before the service has it in its cache.
    private List<LyricsSourceInfo> WithCurrent(IReadOnlyList<LyricsSourceInfo> available)
    {
        List<LyricsSourceInfo> list = [.. available];
        if (SourceKey is { } key && !list.Any(s => s.Key == key) && _lyrics.Sources.FirstOrDefault(s => s.Key == key) is { } current)
        {
            list.Insert(0, current);
        }

        return list;
    }

    private void RememberChoice(string videoId, string key)
    {
        if (!_chosenSource.ContainsKey(videoId))
        {
            _chosenOrder.Enqueue(videoId);
            while (_chosenOrder.Count > ChosenSourceMemory)
            {
                _chosenSource.Remove(_chosenOrder.Dequeue());
            }
        }

        _chosenSource[videoId] = key;
    }

    // A one-shot timer for the next line change, only while synced lines are on screen and the song plays.
    private void UpdateTimer()
    {
        if (!_isActive || !IsSynced || !_isPlaying)
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
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Tick();
        }

        var lines = Lines;
        var position = GetPosition() + LineLead;
        var next = CurrentIndex + 1;
        if (next >= lines.Count)
        {
            _timer.Stop();
            return;
        }

        var due = lines[next].Start - position;
        _timer.Stop();
        _timer.Interval = due > MinTimerInterval ? due : MinTimerInterval;
        _timer.Start();
    }

    private void Tick()
    {
        var lines = Lines;
        if (lines.Count == 0)
        {
            return;
        }

        var position = GetPosition() + LineLead;

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

        CurrentIndex = index;
        UpdateTimer();
    }
}

/// <summary>One item of the synced lyrics: a sung line, or an instrumental break (shown as three dots).</summary>
public sealed class LyricLineViewModel
{
    internal LyricLineViewModel(
        int index,
        TimeSpan start,
        TimeSpan end,
        IReadOnlyList<LyricWordTiming> words,
        IReadOnlyList<LyricWordTiming> backingWords,
        bool isWordTimed,
        bool isEstimated,
        bool isInstrumental)
    {
        Index = index;
        Start = start;
        End = end;
        Words = words;
        BackingWords = backingWords;
        IsWordTimed = isWordTimed;
        IsEstimated = isEstimated;
        IsInstrumental = isInstrumental;
        Text = isInstrumental ? "Instrumental" : LyricsTimeline.Join(words);
    }

    public int Index { get; }

    /// <summary>When the line starts (where a click seeks to).</summary>
    public TimeSpan Start { get; }

    /// <summary>When the last word ends, or the break is over.</summary>
    public TimeSpan End { get; }

    /// <summary>The line as one string (screen readers, tooltips).</summary>
    public string Text { get; }

    /// <summary>The words as laid out. Their times are meaningful only when <see cref="IsWordTimed"/>.</summary>
    public IReadOnlyList<LyricWordTiming> Words { get; }

    /// <summary>Backing vocals sung under the line, shown smaller beneath it.</summary>
    public IReadOnlyList<LyricWordTiming> BackingWords { get; }

    /// <summary>The words light up one by one (timed by the source, or estimated from the line's time).</summary>
    public bool IsWordTimed { get; }

    /// <summary>The word times are spread over the line by length, not given by the source.</summary>
    public bool IsEstimated { get; }

    public bool IsInstrumental { get; }
}

/// <summary>A word (or syllable) and when it is sung. <see cref="SpaceAfter"/> is false inside a word split into syllables.</summary>
public readonly record struct LyricWordTiming(string Text, bool SpaceAfter, TimeSpan Start, TimeSpan End)
{
    public TimeSpan Duration => End - Start;
}

/// <summary>Turns a <see cref="LyricsDocument"/> into the lines the view shows: words, estimated timing, instrumental breaks.</summary>
internal static class LyricsTimeline
{
    /// <summary>A pause between lines at least this long (and an intro before the first line) shows as a break.</summary>
    public static readonly TimeSpan BreakThreshold = TimeSpan.FromSeconds(4);

    // Estimated timing: a line is sung in at most this much time per letter (plus a little), however long the gap to
    // the next line; the rest of the gap is a pause.
    private static readonly TimeSpan PerLetter = TimeSpan.FromMilliseconds(140);
    private static readonly TimeSpan PerLine = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan MinWord = TimeSpan.FromMilliseconds(60);

    public static IReadOnlyList<LyricLineViewModel> Build(LyricsDocument document, bool estimateWords)
    {
        var lines = document.Lines.OrderBy(l => l.Start).ToList();
        List<LyricLineViewModel> items = [];
        var previousEnd = TimeSpan.Zero;
        var previousEndKnown = true; // the song's start
        var i = 0;
        while (i < lines.Count)
        {
            var line = lines[i];
            var nextMain = NextMainStart(lines, i + 1);

            // Backing vocals that open the song have no line to sit under: they show as a line of their own.
            List<LyricLine> backing = [];
            var j = i + 1;
            while (j < lines.Count && lines[j].IsBackground)
            {
                backing.Add(lines[j]);
                j++;
            }

            if (IsBreakMarker(line.Text))
            {
                // An empty line (or just a note sign) marks a pause; a long one shows as a break.
                var end = nextMain ?? line.End;
                if (end - line.Start >= BreakThreshold)
                {
                    items.Add(Break(items.Count, line.Start, end));
                }

                previousEnd = line.Start;
                previousEndKnown = false;
                i = j;
                continue;
            }

            if (previousEndKnown && line.Start - previousEnd >= BreakThreshold && (items.Count == 0 || !items[^1].IsInstrumental))
            {
                items.Add(Break(items.Count, previousEnd, line.Start));
            }

            var (words, wordTimed, estimated) = Words(line, nextMain, estimateWords);
            List<LyricWordTiming> backingWords = [];
            foreach (var b in backing)
            {
                backingWords.AddRange(Words(b, null, estimateWords).Words);
            }

            var sungEnd = wordTimed && words.Count > 0 ? words[^1].End : line.End;
            items.Add(new LyricLineViewModel(items.Count, line.Start, sungEnd, words, backingWords, wordTimed, estimated, isInstrumental: false));

            // A source that gives no end time sets it to the next line's start: the pause is then unknown.
            previousEnd = sungEnd;
            previousEndKnown = line.Words.Count > 0 || (nextMain is { } next && line.End < next - TimeSpan.FromMilliseconds(100));
            i = j;
        }

        return items;
    }

    /// <summary>The words joined back into a line (syllables without spaces between them).</summary>
    public static string Join(IReadOnlyList<LyricWordTiming> words)
    {
        var builder = new System.Text.StringBuilder();
        for (var k = 0; k < words.Count; k++)
        {
            builder.Append(words[k].Text);
            if (words[k].SpaceAfter && k < words.Count - 1)
            {
                builder.Append(' ');
            }
        }

        return builder.ToString();
    }

    private static LyricLineViewModel Break(int index, TimeSpan start, TimeSpan end) =>
        new(index, start, end, [], [], isWordTimed: false, isEstimated: false, isInstrumental: true);

    private static TimeSpan? NextMainStart(List<LyricLine> lines, int from)
    {
        for (var k = from; k < lines.Count; k++)
        {
            if (!lines[k].IsBackground)
            {
                return lines[k].Start;
            }
        }

        return null;
    }

    private static bool IsBreakMarker(string text)
    {
        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static (List<LyricWordTiming> Words, bool Timed, bool Estimated) Words(LyricLine line, TimeSpan? nextStart, bool estimate)
    {
        List<LyricWordTiming> words = [];
        if (line.Words.Count > 0)
        {
            foreach (var word in line.Words)
            {
                var text = word.Text.Trim();
                if (text.Length == 0)
                {
                    if (words.Count > 0)
                    {
                        words[^1] = words[^1] with { SpaceAfter = true };
                    }

                    continue;
                }

                var end = word.End > word.Start + MinWord ? word.End : word.Start + MinWord;
                var spaceAfter = word.Text.Length > 0 && char.IsWhiteSpace(word.Text[^1]);
                words.Add(new LyricWordTiming(text, spaceAfter, word.Start, end));
            }

            if (words.Count > 0)
            {
                return (words, true, false);
            }
        }

        var tokens = Split(line.Text);
        if (tokens.Count == 0)
        {
            return (words, false, false);
        }

        // Spread the line over its words, weighted by length; a line sung briefly before a long pause keeps a pace
        // a singer could manage instead of stretching over the whole pause.
        var available = (nextStart is { } next && next > line.Start ? next : line.End) - line.Start;
        var letters = tokens.Sum(t => Letters(t.Text));
        var natural = PerLine + (PerLetter * letters);
        var sung = available > TimeSpan.Zero ? (available < natural ? available : natural) : natural;
        var total = tokens.Sum(t => Weight(t.Text));
        var at = 0.0;
        foreach (var (text, spaceAfter) in tokens)
        {
            var from = line.Start + (sung * (at / total));
            at += Weight(text);
            var to = line.Start + (sung * (at / total));
            words.Add(new LyricWordTiming(text, spaceAfter, from, to > from + MinWord ? to : from + MinWord));
        }

        return (words, estimate, estimate);
    }

    private static double Weight(string word) => Math.Max(1, Letters(word)) + 0.5;

    private static int Letters(string word)
    {
        var count = 0;
        foreach (var c in word)
        {
            if (char.IsLetterOrDigit(c))
            {
                count++;
            }
        }

        return count;
    }

    // Words split at spaces. Lines of Chinese or Japanese have no spaces: each character is a word there, so the
    // line can wrap and light up character by character.
    private static List<(string Text, bool SpaceAfter)> Split(string text)
    {
        List<(string, bool)> tokens = [];
        foreach (var part in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.Length > 1 && IsSpaceless(part))
            {
                var elements = StringInfo.GetTextElementEnumerator(part);
                var pieces = new List<string>();
                while (elements.MoveNext())
                {
                    pieces.Add(elements.GetTextElement());
                }

                for (var k = 0; k < pieces.Count; k++)
                {
                    tokens.Add((pieces[k], k == pieces.Count - 1));
                }
            }
            else
            {
                tokens.Add((part, true));
            }
        }

        return tokens;
    }

    // CJK ideographs, kana and Thai don't put spaces between words.
    private static bool IsSpaceless(string word)
    {
        var count = 0;
        foreach (var c in word)
        {
            if (c is >= '぀' and <= 'ヿ' or >= '㐀' and <= '鿿' or >= '豈' and <= '﫿' or >= '฀' and <= '๿')
            {
                count++;
            }
        }

        return count * 2 >= word.Length;
    }
}
