using System.ComponentModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Lyrics;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>What a small lyrics view shows.</summary>
public enum CompactLyricsState
{
    /// <summary>A short note in place of lyrics: loading, none, not synced (where plain text doesn't fit), an error.</summary>
    Note,

    /// <summary>Synced lines.</summary>
    Synced,

    /// <summary>Unsynced text.</summary>
    Plain,
}

/// <summary>
/// Lyrics in a small view: the mini player (the line being sung and the next one) or the taskbar player's flyout (the
/// lines around the current one). They come from the shared <see cref="LyricsViewModel"/>, so every view shows the same
/// lyrics, from the same source, loaded once. The view's switch is remembered in settings.
/// </summary>
/// <remarks>
/// Hidden, it costs nothing: the view says when it is on screen (<see cref="SetViewShown"/>), and only then, with the
/// switch on and a song that can have lyrics, does this ask the shared lyrics to load and follow the song, and listen to
/// them. Line changes come from the shared one-shot timer; there is no tick here.
/// </remarks>
public sealed partial class CompactLyricsViewModel : ObservableObject, IDisposable
{
    /// <summary>Shown for an instrumental break, and before the first line.</summary>
    public const string PauseText = "• • •";

    // Two line changes this close together (new lyrics, then the line the song is at) are one jump, not a step.
    private static readonly TimeSpan MinStep = TimeSpan.FromMilliseconds(150);

    private readonly LyricsViewModel _lyrics;
    private readonly IPlayer _player;
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _dispatcher;
    private readonly LyricsViewer _viewer;
    private readonly bool _showsPlainText;
    private readonly Stopwatch _sinceGlance = new();
    private LyricsGlance _glance = LyricsGlance.None;
    private bool _viewShown;
    private bool _listening;
    private bool _disposed;

    /// <param name="viewer">The mini player or the taskbar flyout: which switch is saved, and how the view counts as shown.</param>
    /// <param name="showsPlainText">The view can show unsynced lyrics as scrollable text (else it says they aren't synced).</param>
    public CompactLyricsViewModel(
        LyricsViewModel lyrics,
        IPlayer player,
        ISettingsService settings,
        IUiDispatcher dispatcher,
        LyricsViewer viewer,
        bool showsPlainText)
    {
        _lyrics = lyrics;
        _player = player;
        _settings = settings;
        _dispatcher = dispatcher;
        _viewer = viewer;
        _showsPlainText = showsPlainText;
        IsOn = viewer == LyricsViewer.MiniPlayer ? settings.Current.MiniPlayerLyrics : settings.Current.TaskbarFlyoutLyrics;
        IsAvailable = CanHaveLyrics(player.CurrentTrack);
        player.TrackChanged += OnTrackChanged;
    }

    /// <summary>The track's lines changed (another song, another source): the view lays them out again.</summary>
    public event EventHandler? LinesReplaced;

    /// <summary>The line being sung changed (see <see cref="CompactLyricsLineChangedEventArgs"/>).</summary>
    public event EventHandler<CompactLyricsLineChangedEventArgs>? LineChanged;

    /// <summary>The view's lyrics switch (saved).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowing))]
    public partial bool IsOn { get; private set; }

    /// <summary>A song is playing that can have lyrics (not live radio): the switch shows.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsShowing))]
    public partial bool IsAvailable { get; private set; }

    /// <summary>The lyrics area shows (the switch is on and the song can have lyrics).</summary>
    public bool IsShowing => IsOn && IsAvailable;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSynced), nameof(IsPlain), nameof(HasNote))]
    public partial CompactLyricsState State { get; private set; }

    public bool IsSynced => State == CompactLyricsState.Synced;

    public bool IsPlain => State == CompactLyricsState.Plain;

    public bool HasNote => State == CompactLyricsState.Note;

    /// <summary>"Loading lyrics…", "No lyrics for this song.", …; empty while lyrics show.</summary>
    [ObservableProperty]
    public partial string Note { get; private set; } = string.Empty;

    /// <summary>The line being sung, or <see cref="PauseText"/> in a break.</summary>
    [ObservableProperty]
    public partial string CurrentLine { get; private set; } = string.Empty;

    /// <summary>The next sung line (empty after the last one).</summary>
    [ObservableProperty]
    public partial string NextLine { get; private set; } = string.Empty;

    /// <summary>Index into <see cref="Lines"/> of the item being sung; -1 before the first one.</summary>
    [ObservableProperty]
    public partial int CurrentIndex { get; private set; } = -1;

    /// <summary>Unsynced lyrics, line by line (an empty line between verses), for views that show them.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> PlainLines { get; private set; } = [];

    /// <summary>The synced lines and breaks, in time order (empty while not listening).</summary>
    public IReadOnlyList<LyricLineViewModel> Lines => _listening ? _lyrics.Lines : [];

    /// <summary>The view came on screen (true) or left it (false): mini player shown, flyout open.</summary>
    public void SetViewShown(bool shown)
    {
        _viewShown = shown;
        UpdateListening();
    }

    public void Dispose()
    {
        _disposed = true;
        _player.TrackChanged -= OnTrackChanged;
        UpdateListening();
    }

    [RelayCommand]
    private void Toggle() => SetOn(!IsOn);

    [RelayCommand]
    private void ShowLyrics() => SetOn(true);

    [RelayCommand]
    private void HideLyrics() => SetOn(false);

    private void SetOn(bool on)
    {
        if (on != IsOn)
        {
            IsOn = on;
            var mini = _viewer == LyricsViewer.MiniPlayer;
            _ = _settings.UpdateAsync(s =>
            {
                if (mini)
                {
                    s.MiniPlayerLyrics = on;
                }
                else
                {
                    s.TaskbarFlyoutLyrics = on;
                }
            });
            UpdateListening();
        }
        else
        {
            // Toggle buttons check or uncheck themselves on a click: re-assert the state.
            OnPropertyChanged(nameof(IsOn));
        }
    }

    private static bool CanHaveLyrics(Track? track) => track is { IsLiveRadio: false };

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => _dispatcher.Run(() =>
    {
        if (!_disposed)
        {
            IsAvailable = CanHaveLyrics(e.Track);
            UpdateListening();
        }
    });

    private void UpdateListening()
    {
        var listen = !_disposed && _viewShown && IsShowing;
        if (listen == _listening)
        {
            return;
        }

        _listening = listen;
        if (listen)
        {
            _lyrics.PropertyChanged += OnLyricsPropertyChanged;
            _lyrics.LyricsReplaced += OnLyricsReplaced;
            _lyrics.SetActive(_viewer, true);
            OnLyricsReplaced(this, EventArgs.Empty);
        }
        else
        {
            _lyrics.PropertyChanged -= OnLyricsPropertyChanged;
            _lyrics.LyricsReplaced -= OnLyricsReplaced;
            _lyrics.SetActive(_viewer, false);
        }
    }

    private void OnLyricsReplaced(object? sender, EventArgs e)
    {
        _glance = LyricsGlance.None;
        UpdateState();
        LinesReplaced?.Invoke(this, EventArgs.Empty);
    }

    private void OnLyricsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.CurrentIndex):
                UpdateLine();
                break;
            case nameof(LyricsViewModel.Lines) or nameof(LyricsViewModel.Text) or nameof(LyricsViewModel.IsLoading)
                or nameof(LyricsViewModel.Message) or nameof(LyricsViewModel.ErrorMessage):
                UpdateState();
                break;
        }
    }

    private void UpdateState()
    {
        var lyrics = _lyrics;
        if (lyrics.IsSynced)
        {
            ShowState(CompactLyricsState.Synced, string.Empty);
            UpdateLine();
            return;
        }

        _glance = LyricsGlance.None;
        CurrentIndex = -1;
        CurrentLine = string.Empty;
        NextLine = string.Empty;
        if (lyrics.IsPlain)
        {
            PlainLines = _showsPlainText ? (lyrics.Text ?? string.Empty).Split('\n') : [];
            ShowState(_showsPlainText ? CompactLyricsState.Plain : CompactLyricsState.Note, _showsPlainText ? string.Empty : "Lyrics aren't synced for this song.");
            return;
        }

        PlainLines = [];
        var note = lyrics.IsLoading ? "Loading lyrics…"
            : lyrics.ShowsError ? "Couldn't load the lyrics."
            : lyrics.Message ?? "No lyrics for this song.";
        ShowState(CompactLyricsState.Note, note);
    }

    private void ShowState(CompactLyricsState state, string note)
    {
        Note = note;
        State = state;
    }

    private void UpdateLine()
    {
        var lines = _lyrics.Lines;
        CurrentIndex = _lyrics.CurrentIndex;
        if (lines.Count == 0)
        {
            return;
        }

        var glance = LyricsCursor.Glance(lines, _lyrics.CurrentIndex);
        if (glance == _glance)
        {
            return;
        }

        var follows = glance.Follows(_glance) && _sinceGlance.Elapsed >= MinStep;
        var previousLine = CurrentLine;
        _glance = glance;
        _sinceGlance.Restart();
        CurrentLine = glance.IsPause ? PauseText : lines[glance.Current].Text;
        NextLine = glance.Next >= 0 ? lines[glance.Next].Text : string.Empty;
        LineChanged?.Invoke(this, new CompactLyricsLineChangedEventArgs(previousLine, follows));
    }
}

/// <summary>The line being sung changed.</summary>
/// <param name="PreviousLine">The line shown before (for a view that lets it leave smoothly).</param>
/// <param name="Follows">The song moved on to the next line (animate); false for a jump (a seek, new lyrics).</param>
public sealed class CompactLyricsLineChangedEventArgs(string previousLine, bool follows) : EventArgs
{
    public string PreviousLine { get; } = previousLine;

    public bool Follows { get; } = follows;
}
