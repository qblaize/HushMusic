using System.ComponentModel;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI.ViewManagement;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.NowPlaying;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The full-window Now Playing sheet. Logic lives in <see cref="NowPlayingViewModel"/>; this is UI glue: the slide
/// up / down, fitting the artwork to the window, seek drags, focus in and back out.
/// </summary>
public sealed partial class NowPlayingView : UserControl
{
    private const double MaxArtSize = 560;
    private const double MinArtSize = 120;
    private const double MinControlsWidth = 340;

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(460);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(320);

    private readonly UISettings _uiSettings = new();
    private DependencyObject? _restoreFocus;
    private int _version;

    public NowPlayingView()
    {
        InitializeComponent();
        Backdrop.Animate = false;
        HeroArt.Animate = false;

        // Slider marks pointer events handled, so listen with handledEventsToo to know when a drag starts and ends.
        SeekSlider.AddHandler(PointerPressedEvent, new PointerEventHandler((_, _) => ViewModel.Player.BeginSeek()), handledEventsToo: true);
        SeekSlider.AddHandler(PointerReleasedEvent, new PointerEventHandler((_, _) => ViewModel.Player.EndSeek()), handledEventsToo: true);
        SeekSlider.AddHandler(PointerCaptureLostEvent, new PointerEventHandler((_, _) => ViewModel.Player.EndSeek()), handledEventsToo: true);

        Loaded += (_, _) =>
        {
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            if (ViewModel.IsOpen && Visibility != Visibility.Visible)
            {
                Show();
            }
        };
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public NowPlayingViewModel ViewModel { get; } = App.GetService<NowPlayingViewModel>();

    /// <summary>Buttons in the window's title band: the window must let clicks through to them.</summary>
    public IReadOnlyList<FrameworkElement> TitleBarButtons => [CloseButton, PanelToggleButton];

    /// <summary>
    /// True once the sheet fully covers the shell (the shell can hide what is underneath), false as soon as it starts
    /// to close.
    /// </summary>
    public event EventHandler<bool>? CoverChanged;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(NowPlayingViewModel.IsOpen):
                if (ViewModel.IsOpen)
                {
                    Show();
                }
                else
                {
                    Hide();
                }

                break;
            case nameof(NowPlayingViewModel.Tab):
                UpdateShownTab();
                break;
            case nameof(NowPlayingViewModel.ShowsPanel):
                UpdateShownTab();
                AnimatePanel(ViewModel.ShowsPanel, animate: Visibility == Visibility.Visible);
                break;
            case nameof(NowPlayingViewModel.IsMinimal):
                // Player layout switched while the sheet is open: the backdrop drifts in Standard only.
                if (ViewModel.IsOpen && Visibility == Visibility.Visible)
                {
                    Backdrop.SetDrifting(ViewModel.IsStandard);
                }

                break;
        }
    }

    private async void Show()
    {
        var version = ++_version;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is not null && !IsInside(focused))
        {
            _restoreFocus = focused;
        }

        // Start from the hidden pose, so nothing flashes while the sheet is being laid out.
        SetPose(opacity: 0f, SlideOffset());
        Visibility = Visibility.Visible;

        // Art changes while hidden were applied without decoding; load them properly now, then fade on later changes.
        HeroArt.Refresh();
        Backdrop.Refresh();
        HeroArt.Animate = true;
        Backdrop.Animate = true;
        Covers.SetShown(true);
        UpdateShownTab();

        // The first layout of the sheet is the expensive part. Do it, let it reach the screen, and only then start the
        // motion: started earlier, the animation's first frames were spent waiting on layout and it visibly jumped.
        UpdateLayout();
        await NextFramesAsync();
        if (version != _version)
        {
            return;
        }

        Backdrop.SetDrifting(ViewModel.IsStandard);
        Animate(opening: true, version);
        PlayPauseButton.Focus(FocusState.Programmatic);
    }

    private async void Hide()
    {
        var version = ++_version;

        // The shell shows its content again under the sheet; lay it out before the sheet starts to move (same reason).
        CoverChanged?.Invoke(this, false);
        UpdateShownTab();
        (XamlRoot?.Content as UIElement ?? this).UpdateLayout();
        await NextFramesAsync();
        if (version != _version)
        {
            return;
        }

        Animate(opening: false, version);
        RestoreFocus();
    }

    // Two rendered frames: the one carrying the new layout, and one more so the compositor has it before the motion.
    private static Task NextFramesAsync()
    {
        var done = new TaskCompletionSource();
        var frames = 0;
        void OnRendering(object? sender, object e)
        {
            if (++frames >= 2)
            {
                CompositionTarget.Rendering -= OnRendering;
                done.TrySetResult();
            }
        }

        CompositionTarget.Rendering += OnRendering;
        return done.Task;
    }

    private Vector3 SlideOffset()
    {
        var height = ActualHeight > 0 ? ActualHeight : XamlRoot?.Size.Height ?? 800;
        return new Vector3(0, (float)Math.Min(height * 0.16, 150), 0);
    }

    private Visual RootVisual()
    {
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);
        return ElementCompositionPreview.GetElementVisual(Root);
    }

    private void SetPose(float opacity, Vector3 translation)
    {
        var visual = RootVisual();
        visual.StopAnimation(nameof(Visual.Opacity));
        visual.StopAnimation("Translation");
        visual.Opacity = opacity;
        visual.Properties.InsertVector3("Translation", translation);
    }

    private void Finish(bool opening, int version)
    {
        if (version != _version)
        {
            return;
        }

        if (opening)
        {
            CoverChanged?.Invoke(this, true);
            return;
        }

        Visibility = Visibility.Collapsed;
        Backdrop.SetDrifting(false);
        HeroArt.Animate = false;
        Backdrop.Animate = false;
        Covers.SetShown(false);
    }

    private void UpdateShownTab()
    {
        var panel = ViewModel.IsOpen && ViewModel.ShowsPanel;
        UpNext.SetShown(panel && ViewModel.IsUpNextTab);
        Lyrics.SetShown(panel && ViewModel.IsLyricsTab);
    }

    // Open: a long, soft deceleration (ease-out quint) with the fade done in the first half, so the sheet is solid
    // while it settles. Close: ease-in-out, holding full opacity for the first third, so it slides away rather than
    // dissolving in place. Explicit Composition animations only (they run on the compositor, off the UI thread).
    private void Animate(bool opening, int version)
    {
        var visual = RootVisual();
        var offset = SlideOffset();
        if (!_uiSettings.AnimationsEnabled)
        {
            SetPose(opening ? 1f : 0f, Vector3.Zero);
            Finish(opening, version);
            return;
        }

        var compositor = visual.Compositor;
        var slideEasing = opening
            ? compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f))
            : compositor.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0f), new Vector2(0.35f, 1f));
        var fadeEasing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 0f), new Vector2(0.67f, 1f));
        var duration = opening ? OpenDuration : CloseDuration;

        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Duration = duration;
        if (opening)
        {
            fade.InsertKeyFrame(0f, 0f);
            fade.InsertKeyFrame(0.5f, 1f, fadeEasing);
            fade.InsertKeyFrame(1f, 1f);
        }
        else
        {
            fade.InsertKeyFrame(0f, 1f);
            fade.InsertKeyFrame(0.35f, 1f);
            fade.InsertKeyFrame(1f, 0f, fadeEasing);
        }

        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Duration = duration;
        slide.InsertKeyFrame(0f, opening ? offset : Vector3.Zero);
        slide.InsertKeyFrame(1f, opening ? Vector3.Zero : offset, slideEasing);

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation("Translation", slide);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => Finish(opening, version));
    }

    private void RestoreFocus()
    {
        var target = _restoreFocus;
        _restoreFocus = null;
        if (target is Control { IsLoaded: true, Visibility: Visibility.Visible, IsEnabled: true } control)
        {
            control.Focus(FocusState.Programmatic);
        }
    }

    private bool IsInside(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    // ===== Layout: the artwork takes whatever height the controls leave, up to 560; the Cover Flow spans the column =====

    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;
        _panelWidth = width >= 1360 ? 420 : width >= 1100 ? 380 : 340;
        ApplyPanel();
    }

    // ===== Side panel show / hide (Cover Flow mode, Minimal layout) =====
    // The column width itself is tweened frame by frame, so the covers re-centre smoothly into the freed space
    // (Cover Flow lays out instantly on size changes).

    private static readonly TimeSpan PanelDuration = TimeSpan.FromMilliseconds(320);

    private readonly System.Diagnostics.Stopwatch _panelClock = new();
    private double _panelWidth = 380;
    private double _panelProgress = 1;
    private double _panelFrom = 1;
    private double _panelTo = 1;
    private bool _panelAnimating;

    private void AnimatePanel(bool show, bool animate)
    {
        _panelTo = show ? 1 : 0;
        if (!animate || !_uiSettings.AnimationsEnabled)
        {
            StopPanelAnimation();
            _panelProgress = _panelTo;
            ApplyPanel();
            return;
        }

        _panelFrom = _panelProgress;
        _panelClock.Restart();
        if (!_panelAnimating)
        {
            _panelAnimating = true;
            CompositionTarget.Rendering += OnPanelFrame;
        }
    }

    private void OnPanelFrame(object? sender, object e)
    {
        var t = Math.Clamp(_panelClock.Elapsed / PanelDuration, 0, 1);
        var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        _panelProgress = _panelFrom + ((_panelTo - _panelFrom) * eased);
        ApplyPanel();
        if (t >= 1)
        {
            StopPanelAnimation();
        }
    }

    private void StopPanelAnimation()
    {
        if (_panelAnimating)
        {
            CompositionTarget.Rendering -= OnPanelFrame;
            _panelAnimating = false;
        }

        _panelClock.Stop();
    }

    private void ApplyPanel()
    {
        if (!_panelAnimating)
        {
            _panelProgress = ViewModel.ShowsPanel ? 1 : 0;
        }

        // The panel keeps its full width (no reflow of the lists each frame) and is pushed out past the window's right
        // edge as its column shrinks.
        PanelColumn.Width = new GridLength(Math.Round(_panelWidth * _panelProgress));
        TabsPanel.Width = Math.Max(0, _panelWidth - 16);
        TabsPanel.Opacity = _panelProgress;
        TabsPanel.Visibility = _panelProgress > 0.001 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPlayerAreaSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var area = e.NewSize;
        var side = area.Width >= 720 ? 48 : 32;
        PlayerArea.Padding = new Thickness(side, 0, side, 28);

        var availableWidth = Math.Max(0, area.Width - (2 * side));
        var availableHeight = Math.Max(0, area.Height - 28);
        var controlsWidth = Math.Min(availableWidth, Math.Max(MinControlsWidth, Math.Min(MaxArtSize, availableWidth)));
        ControlsPanel.Measure(new Size(controlsWidth, double.PositiveInfinity));

        var art = Math.Floor(Math.Clamp(Math.Min(availableWidth, availableHeight - ControlsPanel.DesiredSize.Height), MinArtSize, MaxArtSize));
        ArtFrame.Width = art;
        ArtFrame.Height = art;
        PlayerStack.Width = Math.Min(availableWidth, Math.Max(art, MinControlsWidth));

        // Negative margins (no layout clip) take the Cover Flow out to both edges of the column, centred on the cover.
        var overhang = Math.Max(0, Math.Floor((area.Width - PlayerStack.Width) / 2));
        Covers.Height = art;
        Covers.Margin = new Thickness(-overhang, 0, -overhang, 0);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.Player.Track is { } track)
        {
            TrackMenu.ShowAt(MoreButton, track);
        }
    }
}
