using Microsoft.UI.Xaml.Input;
using HushMusic.App.Controls.NowPlaying;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.App.ViewModels.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Controls;

/// <summary>
/// The player bar of the Minimal layout: docked under the pages, transport in the middle, volume and a "More" menu
/// (like, shuffle, repeat, lyrics, up next, sleep timer, mini player) on the right. Logic lives in
/// <see cref="PlayerViewModel"/> and <see cref="NowPlayingViewModel"/>; this is UI glue: the progress line that turns
/// into a seek slider, seek drags, the menu's check marks and dropped tracks.
/// </summary>
public sealed partial class MinimalPlayerBar : UserControl
{
    private bool _pointerOver;
    private bool _dragging;
    private bool _keyboardFocus;

    public MinimalPlayerBar()
    {
        InitializeComponent();

        // Slider marks pointer events handled, so listen with handledEventsToo to know when a drag starts and ends.
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler(OnSeekPointerPressed), handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler(OnSeekPointerReleased), handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler(OnSeekPointerReleased), handledEventsToo: true);
    }

    public PlayerViewModel ViewModel { get; } = App.GetService<PlayerViewModel>();

    public NowPlayingViewModel NowPlaying { get; } = App.GetService<NowPlayingViewModel>();

    /// <summary>The played part of the track, 0 to 1: the width of the progress line.</summary>
    public double Fraction(double position, double duration) =>
        duration > 0 && double.IsFinite(position) ? Math.Clamp(position / duration, 0, 1) : 0;

    // ===== Progress line / seek slider =====

    private void OnProgressPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _pointerOver = true;
        UpdateProgressState();
    }

    // Exited also bubbles up from the slider inside, and capture ends when a drag does: only the host's own bounds count.
    private void OnProgressPointerLeft(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(ProgressHost).Position;
        _pointerOver = point.X >= 0 && point.Y >= 0 && point.X < ProgressHost.ActualWidth && point.Y < ProgressHost.ActualHeight;
        UpdateProgressState();
    }

    private void OnSeekPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        ViewModel.BeginSeek();
        UpdateProgressState();
    }

    private void OnSeekPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        ViewModel.EndSeek();
        if (_dragging)
        {
            _dragging = false;
            OnProgressPointerLeft(sender, e);
        }
    }

    // Keyboard focus shows the slider (arrow keys seek); a click focuses it too, but then the pointer decides.
    private void OnSeekFocusChanged(object sender, RoutedEventArgs e)
    {
        _keyboardFocus = SeekSlider.FocusState == FocusState.Keyboard;
        UpdateProgressState();
    }

    private void OnSeekEnabledChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateProgressState();

    private void UpdateProgressState()
    {
        var shown = SeekSlider.IsEnabled && (_pointerOver || _dragging || _keyboardFocus);
        VisualStateManager.GoToState(this, shown ? "SeekSliderShown" : "ProgressLineShown", useTransitions: true);
    }

    // ===== More menu =====

    // Repeat and the sleep timer have one checked choice each, read fresh on every open.
    private void OnMoreMenuOpening(object? sender, object e)
    {
        var repeat = ViewModel.Repeat;
        RepeatOffItem.IsChecked = repeat == RepeatMode.Off;
        RepeatAllItem.IsChecked = repeat == RepeatMode.All;
        RepeatOneItem.IsChecked = repeat == RepeatMode.One;
        SleepTimerMenuFlyout.Fill(SleepTimerItem.Items, NowPlaying.SleepTimer);
    }

    private void OnRepeatClick(object sender, RoutedEventArgs e) => ViewModel.SetRepeat(
        ReferenceEquals(sender, RepeatAllItem) ? RepeatMode.All
        : ReferenceEquals(sender, RepeatOneItem) ? RepeatMode.One
        : RepeatMode.Off);

    // ===== Tracks dropped on the bar go to the queue =====

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (PlayerBar.AcceptTrackDrag(e))
        {
            DropHighlight.Visibility = Visibility.Visible;
        }
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHighlight.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHighlight.Visibility = Visibility.Collapsed;
        await PlayerBar.AddDroppedTracksAsync(e, NowPlaying);
    }
}
