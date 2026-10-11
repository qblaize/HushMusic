using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.YearInReview;

namespace HushMusic.App.Controls.YearInReview;

/// <summary>
/// "Your year in Hush", over the whole window. Logic lives in <see cref="YearInReviewViewModel"/>; this is UI glue: the
/// card's size, the slide transitions, the progress bars and their timing, and saving a slide as an image.
/// The shell creates the view when the story opens and drops it once it has closed, so nothing stays behind.
/// </summary>
/// <remarks>
/// The timeline (the current slide's progress bar and the move to the next one) runs only while the view is open, not
/// paused, and the window is on screen (<see cref="AnimationGate"/>); otherwise it holds where it is.
/// </remarks>
public sealed partial class YearInReviewView : UserControl
{
    /// <summary>The card is laid out at this size and scaled to fit the window.</summary>
    private const double CardWidth = 480;
    private const double CardHeight = 600;
    private const float CardCornerRadius = 24;

    /// <summary>Saved images are 1080 pixels wide (1080 × 1350), the size social apps expect.</summary>
    private const int ImageWidth = 1080;

    // The blurred art never shows at full strength: a white cover would leave white text nothing to stand on.
    private const float ArtOpacity = 0.75f;

    // Room for the chevrons beside the card, and for the header above it.
    private const double SideRoom = 96;
    private const double HeaderRoom = 76;
    private const double BottomRoom = 28;

    private static readonly TimeSpan OpenDuration = TimeSpan.FromMilliseconds(520);
    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(260);
    private static readonly TimeSpan SlideInDuration = TimeSpan.FromMilliseconds(560);
    private static readonly TimeSpan SlideOutDuration = TimeSpan.FromMilliseconds(220);
    private static readonly TimeSpan ArtFadeDuration = TimeSpan.FromMilliseconds(700);

    private readonly UISettings _uiSettings = new();
    private readonly AnimationGate _gate;
    private readonly Stopwatch _slideClock = new();
    private readonly List<Border> _fills = [];
    private DispatcherQueueTimer? _advanceTimer;
    private ContentPresenter? _presenter;
    private DependencyObject? _restoreFocus;
    private TimeSpan _elapsed;
    private string? _artUrl;
    private int _shownIndex = -1;
    private int _version;
    private bool _showsArtA = true;
    private bool _opened;
    private bool _saving;

    public YearInReviewView()
    {
        InitializeComponent();
        _gate = new AnimationGate(this, OnGateChanged);

        // Faded in on the compositor when there is something to show.
        VisualOf(CardArtA).Opacity = 0;
        VisualOf(CardArtB).Opacity = 0;
        VisualOf(AccentWash).Opacity = 0;

        // Rounded corners for the card (the saved image keeps square ones: it is drawn from CardSurface).
        var frame = VisualOf(CardFrame);
        var corners = frame.Compositor.CreateRoundedRectangleGeometry();
        corners.Size = new Vector2((float)CardWidth, (float)CardHeight);
        corners.CornerRadius = new Vector2(CardCornerRadius, CardCornerRadius);
        frame.Clip = frame.Compositor.CreateGeometricClip(corners);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public YearInReviewViewModel ViewModel { get; } = App.GetService<YearInReviewViewModel>();

    private bool Animates => _uiSettings.AnimationsEnabled;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        _advanceTimer ??= DispatcherQueue.CreateTimer();
        _advanceTimer.IsRepeating = false;
        _advanceTimer.Tick += OnAdvanceTimerTick;
        if (ViewModel.IsOpen)
        {
            Show();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        if (_advanceTimer is not null)
        {
            _advanceTimer.Stop();
            _advanceTimer.Tick -= OnAdvanceTimerTick;
        }

        _gate.IsWanted = false;
        if (_presenter is not null)
        {
            RecapMotion.Stop(_presenter);
        }

        SlideHost.Children.Clear();
        _presenter = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(YearInReviewViewModel.IsOpen):
                if (ViewModel.IsOpen)
                {
                    Show();
                }
                else
                {
                    Hide();
                }

                break;
            case nameof(YearInReviewViewModel.Index):
                if (_opened)
                {
                    ShowSlide(ViewModel.Index);
                }

                break;
            case nameof(YearInReviewViewModel.IsPaused):
                UpdateGate();
                break;
        }
    }

    // ===== Opening and closing =====

    private void Show()
    {
        var version = ++_version;
        var focused = XamlRoot is null ? null : FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
        if (focused is not null && !IsInside(focused))
        {
            _restoreFocus = focused;
        }

        ApplyAccent(ViewModel.Accent);
        BuildSegments();
        _shownIndex = -1;
        _opened = true;
        ShowSlide(ViewModel.Index);
        NextButton.Focus(FocusState.Programmatic);

        var root = VisualOf(Root);
        var column = VisualOf(Column);
        if (!Animates)
        {
            root.StopAnimation(nameof(Visual.Opacity));
            column.StopAnimation(nameof(Visual.Scale));
            root.Opacity = 1;
            column.Scale = Vector3.One;
            Finish();
            return;
        }

        // The whole view fades in while the card settles from slightly larger, like a page coming forward.
        var compositor = root.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 0f), new Vector2(0.67f, 1f)));
        fade.Duration = OpenDuration * 0.6;
        column.CenterPoint = new Vector3((float)Column.ActualWidth / 2, (float)Column.ActualHeight / 2, 0);
        var settle = compositor.CreateVector3KeyFrameAnimation();
        settle.InsertKeyFrame(0f, new Vector3(1.04f, 1.04f, 1));
        settle.InsertKeyFrame(1f, Vector3.One, RecapMotion.Ease(compositor));
        settle.Duration = OpenDuration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        root.StartAnimation(nameof(Visual.Opacity), fade);
        column.StartAnimation(nameof(Visual.Scale), settle);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(Finish);

        void Finish()
        {
            if (version == _version)
            {
                ViewModel.CoversShell = true;
                UpdateGate();
            }
        }
    }

    private void Hide()
    {
        var version = ++_version;
        _opened = false;
        UpdateGate();
        ViewModel.CoversShell = false;
        RestoreFocus();

        if (!Animates)
        {
            ViewModel.OnClosed();
            return;
        }

        var root = VisualOf(Root);
        var compositor = root.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f, compositor.CreateCubicBezierEasingFunction(new Vector2(0.33f, 0f), new Vector2(0.67f, 1f)));
        fade.Duration = CloseDuration;
        var shrink = compositor.CreateVector3KeyFrameAnimation();
        shrink.InsertKeyFrame(1f, new Vector3(0.97f, 0.97f, 1));
        shrink.Duration = CloseDuration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        root.StartAnimation(nameof(Visual.Opacity), fade);
        VisualOf(Column).StartAnimation(nameof(Visual.Scale), shrink);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() =>
        {
            if (version == _version)
            {
                ViewModel.OnClosed();
            }
        });
    }

    private void RestoreFocus()
    {
        var target = _restoreFocus;
        _restoreFocus = null;
        if (target is Control { IsLoaded: true, IsEnabled: true } control)
        {
            control.Focus(FocusState.Programmatic);
        }
    }

    // ===== The card's size: as large as the window allows, keeping its shape =====

    private void OnStageSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var scale = Math.Min(
            (e.NewSize.Width - (2 * SideRoom)) / CardWidth,
            (e.NewSize.Height - HeaderRoom - BottomRoom) / CardHeight);
        scale = Math.Clamp(scale, 0.45, 2.4);
        CardHost.Width = Math.Floor(CardWidth * scale);
        CardHost.Height = Math.Floor(CardHeight * scale);
    }

    // ===== Slides =====

    private void ShowSlide(int index)
    {
        var slides = ViewModel.Slides;
        if (index < 0 || index >= slides.Count)
        {
            return;
        }

        var direction = _shownIndex < 0 || index >= _shownIndex ? 1 : -1;
        var restart = index == _shownIndex;
        _shownIndex = index;
        var slide = slides[index];

        ShowArt(slide.BackdropUrl);
        AnimateOpacity(AccentWash, slide.BackdropUrl is null ? 1f : 0.3f, _presenter is null || !Animates ? TimeSpan.Zero : ArtFadeDuration);

        var previous = _presenter;
        var presenter = new ContentPresenter
        {
            Content = slide,
            ContentTemplateSelector = (DataTemplateSelector)Resources["SlideTemplates"],
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        _presenter = presenter;
        SlideHost.Children.Add(presenter);
        presenter.UpdateLayout();

        if (previous is not null)
        {
            SlideOut(previous, restart ? 0 : direction);
        }

        if (Animates)
        {
            var visual = VisualOf(presenter);
            var compositor = visual.Compositor;
            var slideIn = compositor.CreateVector3KeyFrameAnimation();
            slideIn.InsertKeyFrame(0f, new Vector3(restart ? 0 : 36 * direction, 0, 0));
            slideIn.InsertKeyFrame(1f, Vector3.Zero, RecapMotion.Ease(compositor));
            slideIn.Duration = SlideInDuration;
            visual.StartAnimation("Translation", slideIn);
            RecapMotion.Enter(presenter);
        }

        // Screen readers hear the new slide.
        AutomationProperties.SetName(SlideHost, slide.Description);
        FrameworkElementAutomationPeer.FromElement(SlideHost)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);

        RestartTimeline();
    }

    private void SlideOut(ContentPresenter presenter, int direction)
    {
        presenter.IsHitTestVisible = false;
        RecapMotion.Stop(presenter);
        if (!Animates)
        {
            SlideHost.Children.Remove(presenter);
            return;
        }

        var visual = VisualOf(presenter);
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = SlideOutDuration;
        var move = compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(1f, new Vector3(-24 * direction, 0, 0));
        move.Duration = SlideOutDuration;

        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation(nameof(Visual.Opacity), fade);
        visual.StartAnimation("Translation", move);
        batch.End();
        batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => SlideHost.Children.Remove(presenter));
    }

    // The blurred art behind the card cross-fades between two images, once the new one has loaded.
    private void ShowArt(string? url)
    {
        if (string.Equals(url, _artUrl, StringComparison.Ordinal))
        {
            return;
        }

        _artUrl = url;
        var fade = Animates ? ArtFadeDuration : TimeSpan.Zero;
        var shown = _showsArtA ? CardArtA : CardArtB;
        if (url is null)
        {
            AnimateOpacity(shown, 0f, fade);
            return;
        }

        if (string.Equals(ImageLoader.GetUrl(shown), url, StringComparison.Ordinal))
        {
            // Back to the art of before a slide without any.
            AnimateOpacity(shown, ArtOpacity, fade);
            return;
        }

        _showsArtA = !_showsArtA;
        var next = _showsArtA ? CardArtA : CardArtB;
        void Swap()
        {
            AnimateOpacity(next, ArtOpacity, fade);
            AnimateOpacity(shown, 0f, fade);
        }

        if (string.Equals(ImageLoader.GetUrl(next), url, StringComparison.Ordinal) && next.Source is not null)
        {
            Swap();
            return;
        }

        void OnOpened(object sender, RoutedEventArgs e)
        {
            next.ImageOpened -= OnOpened;
            if (string.Equals(_artUrl, url, StringComparison.Ordinal))
            {
                Swap();
            }
        }

        next.ImageOpened += OnOpened;
        ImageLoader.SetUrl(next, url);
    }

    private static void AnimateOpacity(UIElement element, float to, TimeSpan duration)
    {
        var visual = VisualOf(element);
        if (duration <= TimeSpan.Zero)
        {
            visual.StopAnimation(nameof(Visual.Opacity));
            visual.Opacity = to;
            return;
        }

        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, to);
        fade.Duration = duration;
        visual.StartAnimation(nameof(Visual.Opacity), fade);
    }

    private void ApplyAccent(Color? accent)
    {
        var color = accent ?? (Application.Current.Resources["AccentOnDarkBrush"] as SolidColorBrush)?.Color ?? ((SolidColorBrush)Resources["RecapAccentBrush"]).Color;
        ((SolidColorBrush)Resources["RecapAccentBrush"]).Color = color;
        WashStart.Color = AccentPalette.WithAlpha(color, 0xB3);
        WashEnd.Color = AccentPalette.WithAlpha(color, 0x1A);
    }

    // ===== Progress segments and timing =====

    private void BuildSegments()
    {
        Segments.Children.Clear();
        Segments.ColumnDefinitions.Clear();
        _fills.Clear();
        var track = ThemeResources.Get<Brush>("SeparatorBrush", ElementTheme.Dark);
        var fill = ThemeResources.Get<Brush>("TextPrimaryBrush", ElementTheme.Dark);
        for (var i = 0; i < ViewModel.Slides.Count; i++)
        {
            Segments.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var bar = new Border { Background = fill, CornerRadius = new CornerRadius(1.5) };
            var segment = new Grid { Background = track, CornerRadius = new CornerRadius(1.5), Children = { bar } };
            Grid.SetColumn(segment, i);
            Segments.Children.Add(segment);
            _fills.Add(bar);
            VisualOf(bar).Scale = new Vector3(0, 1, 1);
        }
    }

    private void RestartTimeline()
    {
        PauseTimeline();
        _elapsed = TimeSpan.Zero;
        for (var i = 0; i < _fills.Count; i++)
        {
            SetFill(i, i < _shownIndex ? 1 : 0);
        }

        UpdateGate();
        if (_gate.IsOpen)
        {
            ResumeTimeline();
        }
    }

    private void UpdateGate() => _gate.IsWanted = _opened && ViewModel.IsOpen && !ViewModel.IsPaused && ViewModel.HasSlides && !_saving;

    private void OnGateChanged(bool open)
    {
        if (open)
        {
            ResumeTimeline();
        }
        else
        {
            PauseTimeline();
        }
    }

    // The current bar fills on the compositor; the timer moves on when it is full.
    private void ResumeTimeline()
    {
        if (_slideClock.IsRunning || _shownIndex < 0 || _shownIndex >= _fills.Count || _advanceTimer is null)
        {
            return;
        }

        var duration = YearInReviewViewModel.SlideDuration;
        var remaining = duration - _elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            SetFill(_shownIndex, 1);
            return;
        }

        _slideClock.Restart();
        _advanceTimer.Interval = remaining;
        _advanceTimer.Start();

        var visual = VisualOf(_fills[_shownIndex]);
        var grow = visual.Compositor.CreateScalarKeyFrameAnimation();
        grow.InsertKeyFrame(0f, (float)(_elapsed / duration));
        grow.InsertKeyFrame(1f, 1f, visual.Compositor.CreateLinearEasingFunction());
        grow.Duration = remaining;
        visual.StartAnimation("Scale.X", grow);
    }

    private void PauseTimeline()
    {
        _advanceTimer?.Stop();
        if (!_slideClock.IsRunning)
        {
            return;
        }

        _elapsed += _slideClock.Elapsed;
        _slideClock.Reset();
        if (_shownIndex >= 0 && _shownIndex < _fills.Count)
        {
            SetFill(_shownIndex, Math.Min(1, _elapsed / YearInReviewViewModel.SlideDuration));
        }
    }

    private void SetFill(int index, double amount)
    {
        var visual = VisualOf(_fills[index]);
        visual.StopAnimation("Scale.X");
        visual.Scale = new Vector3((float)amount, 1, 1);
    }

    private void OnAdvanceTimerTick(DispatcherQueueTimer sender, object args)
    {
        sender.Stop();
        PauseTimeline();
        _elapsed = YearInReviewViewModel.SlideDuration;
        if (_shownIndex >= 0 && _shownIndex < _fills.Count)
        {
            SetFill(_shownIndex, 1);
        }

        // The last slide stays (the summary is the one to save).
        ViewModel.Next();
    }

    // ===== Save image =====

    private async void OnSaveClick(object sender, RoutedEventArgs e)
    {
        if (_saving || _presenter is null)
        {
            return;
        }

        // The slide stays put while it is drawn, its entrance finished.
        _saving = true;
        UpdateGate();
        RecapMotion.Settle(_presenter);
        string? path = null;
        try
        {
            path = ViewModel.ImagePathForCurrent();
            await SlideImage.SaveAsync(CardSurface, ImageWidth, path);
            ViewModel.ReportSaved(path);
        }
        catch (Exception ex)
        {
            ViewModel.ReportSaveFailed(ex);
        }
        finally
        {
            _saving = false;
            UpdateGate();
        }
    }

    // ===== Helpers =====

    private static Visual VisualOf(UIElement element)
    {
        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        return ElementCompositionPreview.GetElementVisual(element);
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
}
