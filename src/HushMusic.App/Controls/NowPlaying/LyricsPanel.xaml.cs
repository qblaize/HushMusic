using System.ComponentModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The "Lyrics" tab. Logic lives in <see cref="LyricsViewModel"/>; this is scroll glue: synced lyrics follow the song
/// (current line about a third of the way down), and scrolling by hand pauses that for a few seconds.
/// </summary>
public sealed partial class LyricsPanel : UserControl
{
    private static readonly TimeSpan ManualScrollPause = TimeSpan.FromSeconds(4);

    private readonly DispatcherQueueTimer _resumeTimer;
    private DateTime _followPausedUntil;
    private double? _scrollTarget;
    private bool _isShown;

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

        SyncedScroller.AddHandler(PointerWheelChangedEvent, new PointerEventHandler((_, _) => PauseFollow()), handledEventsToo: true);
        SyncedScroller.DirectManipulationStarted += (_, _) => PauseFollow();
        SyncedScroller.ViewChanging += OnViewChanging;
        SyncedScroller.ViewChanged += (_, e) =>
        {
            if (!e.IsIntermediate)
            {
                _scrollTarget = null;
            }
        };
        SyncedScroller.SizeChanged += (_, e) => UpdatePadding(e.NewSize.Height);

        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.LyricsReplaced += OnLyricsReplaced;
        };
        Unloaded += (_, _) =>
        {
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            ViewModel.LyricsReplaced -= OnLyricsReplaced;
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
            DispatcherQueue.TryEnqueue(() => ScrollToCurrent(animate: false));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsViewModel.CurrentIndex) && _isShown && DateTime.UtcNow >= _followPausedUntil)
        {
            ScrollToCurrent(animate: true);
        }
    }

    private void OnLyricsReplaced(object? sender, EventArgs e)
    {
        _followPausedUntil = default;
        _scrollTarget = SyncedScroller.VerticalOffset > 0 ? 0 : null;
        SyncedScroller.ChangeView(null, 0, null, disableAnimation: true);
        PlainScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnLineClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: LyricLineViewModel line })
        {
            _followPausedUntil = default;
            ViewModel.SeekTo(line);
        }
    }

    // Room above and below so the first and last lines can also sit at the one-third mark.
    private void UpdatePadding(double viewportHeight)
    {
        var padding = SyncedContent.Padding;
        SyncedContent.Padding = new Thickness(padding.Left, padding.Top, padding.Right, Math.Round(viewportHeight * 2 / 3));
    }

    private void ScrollToCurrent(bool animate)
    {
        if (!_isShown || !ViewModel.IsSynced || DateTime.UtcNow < _followPausedUntil)
        {
            return;
        }

        double target = 0;
        var index = ViewModel.CurrentIndex;
        if (index >= 0)
        {
            if (LinesList.ContainerFromIndex(index) is not FrameworkElement line)
            {
                return;
            }

            var top = line.TransformToVisual(SyncedContent).TransformPoint(default).Y;
            target = Math.Clamp(top - (SyncedScroller.ViewportHeight / 3), 0, SyncedScroller.ScrollableHeight);
        }

        if (Math.Abs(target - SyncedScroller.VerticalOffset) < 1)
        {
            return;
        }

        _scrollTarget = target;
        if (!SyncedScroller.ChangeView(null, target, null, disableAnimation: !animate))
        {
            _scrollTarget = null;
        }
    }

    // Any scroll that isn't heading for our own target is the user's.
    private void OnViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
    {
        if (_scrollTarget is { } target && Math.Abs(e.FinalView.VerticalOffset - target) < 1)
        {
            return;
        }

        PauseFollow();
    }

    private void PauseFollow()
    {
        _followPausedUntil = DateTime.UtcNow + ManualScrollPause;
        _scrollTarget = null;
        _resumeTimer.Stop();
        _resumeTimer.Start();
    }
}
