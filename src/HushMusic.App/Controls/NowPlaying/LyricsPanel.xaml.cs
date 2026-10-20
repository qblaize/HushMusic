using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using HushMusic.App.Helpers;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The "Lyrics" tab. Logic lives in <see cref="LyricsViewModel"/> and the timing look in <see cref="LyricsStage"/>; this
/// is glue: it builds the lines when they are first shown, keeps the current line about a third of the way down with a
/// soft, spring-like scroll, and lets scrolling by hand pause that for a few seconds.
/// </summary>
/// <remarks>
/// The animations run only while they can be seen and the music plays (<see cref="AnimationGate"/>): the tab shown, the
/// window on screen. Otherwise the stage holds still at the current position.
/// </remarks>
public sealed partial class LyricsPanel : UserControl
{
    private static readonly TimeSpan ManualScrollPause = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(450);

    // The current line's top sits this far down the view (Better Lyrics keeps it at 37 %).
    private const double FocusRatio = 0.3;

    // Unsynced lyrics: the space under a line, and under the last line of a verse.
    private const double PlainLineGap = 7;
    private const double PlainVerseGap = 24;

    // Automated screenshots can't scroll: HUSHMUSIC_TEST_LYRICS_SCROLL=end keeps synced lyrics at their end (the source
    // line and its menu) instead of following the song.
    private static readonly bool s_testScrollToEnd = Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_LYRICS_SCROLL") == "end";

    private readonly DispatcherQueueTimer _resumeTimer;
    private readonly UISettings _uiSettings = new();
    private readonly LyricsStage _stage;
    private readonly AnimationGate _gate;
    private DateTime _followPausedUntil;
    private int _ownScroll = -1;
    private bool _isShown;
    private bool _linesStale = true;

    public LyricsPanel()
    {
        InitializeComponent();
        _resumeTimer = DispatcherQueue.CreateTimer();
        _resumeTimer.Interval = ManualScrollPause;
        _resumeTimer.IsRepeating = false;
        _resumeTimer.Tick += (_, _) =>
        {
            _followPausedUntil = default;
            ScrollToCurrent(animate: true);
        };

        var buttonStyle = (Style)Resources["LyricLineButtonStyle"];
        var wordStyle = (Style)Resources["LyricWordTextStyle"];
        var backingStyle = (Style)Resources["LyricBackingWordTextStyle"];
        var textBrush = (Brush)Application.Current.Resources["TextPrimaryBrush"];
        var textColor = textBrush is SolidColorBrush solid ? solid.Color : Color.FromArgb(0xFF, 0xF5, 0xF5, 0xF7);
        _stage = new LyricsStage(
            LinesHost,
            ViewModel.GetPosition,
            () =>
            {
                var line = new LyricLineButton(buttonStyle, wordStyle, backingStyle, textBrush);
                line.Click += OnLineClick;
                return line;
            },
            textColor);
        _gate = new AnimationGate(this, _stage.SetRunning);

        SyncedScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => PauseFollow()), handledEventsToo: true);
        SyncedScroller.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => PauseFollow()), handledEventsToo: true);
        SyncedScroller.StateChanged += (sender, _) =>
        {
            if (sender.State == ScrollingInteractionState.Interaction)
            {
                PauseFollow();
            }
        };
        SyncedScroller.ScrollAnimationStarting += OnScrollAnimationStarting;
        SyncedScroller.ScrollCompleted += (_, e) =>
        {
            if (e.CorrelationId == _ownScroll)
            {
                _ownScroll = -1;
            }
        };
        SyncedScroller.SizeChanged += (_, e) => UpdatePadding(e.NewSize.Height);

        // The scroll bar shows only under the pointer: shown, it would also appear (and fade out again) on every line
        // change, and those frames redraw the whole view.
        SyncedScroller.PointerEntered += (_, _) => SyncedScroller.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto;
        SyncedScroller.PointerExited += (_, _) => SyncedScroller.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;
        SyncedScroller.PointerCanceled += (_, _) => SyncedScroller.VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden;

        Loaded += (_, _) =>
        {
            ShowPlainText();
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.LyricsReplaced += OnLyricsReplaced;
            ViewModel.TimingChanged += OnTimingChanged;
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.LyricsReplaced -= OnLyricsReplaced;
            ViewModel.TimingChanged -= OnTimingChanged;
            _resumeTimer.Stop();
        };
    }

    public LyricsViewModel ViewModel { get; } = App.GetService<LyricsViewModel>();

    /// <summary>The tab became visible (true) or hidden (false); visible jumps straight to the current line.</summary>
    public void SetShown(bool shown)
    {
        _isShown = shown;
        _followPausedUntil = default;
        if (shown)
        {
            _stage.MotionEnabled = _uiSettings.AnimationsEnabled;
            if (_linesStale)
            {
                Rebuild();
            }
            else
            {
                _stage.SetCurrent(ViewModel.CurrentIndex);
            }

            DispatcherQueue.TryEnqueue(() => ScrollToCurrent(animate: false));
        }

        UpdateGate();
        _stage.Resync();
    }

    private void UpdateGate() => _gate.IsWanted = _isShown && ViewModel.IsPlaying && _stage.MotionEnabled;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LyricsViewModel.CurrentIndex) when _isShown && !_linesStale:
                _stage.SetCurrent(ViewModel.CurrentIndex);
                if (DateTime.UtcNow >= _followPausedUntil)
                {
                    ScrollToCurrent(animate: true);
                }

                break;
            case nameof(LyricsViewModel.IsPlaying):
                UpdateGate();
                break;
            case nameof(LyricsViewModel.Text):
                ShowPlainText();
                break;
        }
    }

    private void OnTimingChanged(object? sender, EventArgs e)
    {
        if (_isShown)
        {
            _stage.Resync();
        }
    }

    // New lyrics: the lines are built now if the tab shows, otherwise when it is next shown.
    private void OnLyricsReplaced(object? sender, EventArgs e)
    {
        _linesStale = true;
        _followPausedUntil = default;
        if (_isShown)
        {
            Rebuild();
        }

        _ownScroll = -1;
        SyncedScroller.ScrollTo(0, 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        PlainScroller.ChangeView(null, 0, null, disableAnimation: true);
        if (_isShown)
        {
            DispatcherQueue.TryEnqueue(() => ScrollToCurrent(animate: false));
        }
    }

    private void ShowPlainText()
    {
        var blocks = PlainText.Blocks;
        blocks.Clear();
        Paragraph? last = null;
        foreach (var line in (ViewModel.Text ?? string.Empty).Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0)
            {
                if (last is not null)
                {
                    last.Margin = new Thickness(0, 0, 0, PlainVerseGap);
                }

                continue;
            }

            last = new Paragraph { Margin = new Thickness(0, 0, 0, PlainLineGap) };
            last.Inlines.Add(new Run { Text = text });
            blocks.Add(last);
        }
    }

    private void Rebuild()
    {
        _linesStale = false;
        _stage.SetLines(ViewModel.Lines);
        _stage.SetCurrent(ViewModel.CurrentIndex);
    }

    private void OnLineClick(object sender, RoutedEventArgs e)
    {
        if (sender is LyricLineButton { Line: { } line })
        {
            _followPausedUntil = default;
            _resumeTimer.Stop();
            ViewModel.SeekTo(line);
            ScrollToCurrent(animate: true);
        }
    }

    // Room below so the last lines can also reach the focus point.
    private void UpdatePadding(double viewportHeight)
    {
        var padding = SyncedContent.Padding;
        SyncedContent.Padding = new Thickness(padding.Left, padding.Top, padding.Right, Math.Round(viewportHeight * (1 - FocusRatio)));
    }

    private void ScrollToCurrent(bool animate)
    {
        if (!_isShown || !ViewModel.IsSynced || _linesStale || DateTime.UtcNow < _followPausedUntil)
        {
            return;
        }

        double target = 0;
        var index = ViewModel.CurrentIndex;
        var lines = _stage.Lines;
        if (s_testScrollToEnd)
        {
            var footer = SyncedFooter.TransformToVisual(SyncedContent).TransformPoint(default).Y;
            target = Math.Clamp(footer - (SyncedScroller.ViewportHeight / 2), 0, Math.Max(0, SyncedScroller.ScrollableHeight));
        }
        else if (index >= 0 && index < lines.Count)
        {
            var top = lines[index].TransformToVisual(SyncedContent).TransformPoint(default).Y;
            target = Math.Clamp(top - (SyncedScroller.ViewportHeight * FocusRatio), 0, Math.Max(0, SyncedScroller.ScrollableHeight));
        }

        if (Math.Abs(target - SyncedScroller.VerticalOffset) < 1)
        {
            return;
        }

        animate &= _stage.MotionEnabled && WindowPresence.IsShown;
        _ownScroll = SyncedScroller.ScrollTo(
            0,
            target,
            new ScrollingScrollOptions(animate ? ScrollingAnimationMode.Enabled : ScrollingAnimationMode.Disabled));
    }

    // Our own scroll glides with a soft settle (cubic-bezier(0.2, 0, 0, 1)); any other one is the user's.
    private void OnScrollAnimationStarting(ScrollView sender, ScrollingScrollAnimationStartingEventArgs args)
    {
        if (args.CorrelationId != _ownScroll)
        {
            PauseFollow();
            return;
        }

        if (args.Animation is not Vector3KeyFrameAnimation stock)
        {
            return;
        }

        var compositor = stock.Compositor;
        var glide = compositor.CreateVector3KeyFrameAnimation();
        glide.InsertKeyFrame(
            1,
            new Vector3(args.EndPosition.X, args.EndPosition.Y, 0),
            compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0), new Vector2(0, 1)));
        glide.Duration = ScrollDuration;
        args.Animation = glide;
    }

    private void PauseFollow()
    {
        _followPausedUntil = DateTime.UtcNow + ManualScrollPause;
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }
}
