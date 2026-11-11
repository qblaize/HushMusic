using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.TaskbarFlyout;

/// <summary>
/// The flyout's synced lyrics: a line per item, the one being sung bright and the others dimmed, moved up so that line
/// sits at the top, under the one before it when that one is short. The mouse wheel scrolls them by hand, which pauses
/// the following for a few seconds.
/// </summary>
/// <remarks>
/// The lines are laid out once; a change of line fades two of them (Composition) and moves the list in one step (a
/// Composition offset, no layout). The list doesn't glide: redrawing the moving list over the flyout's acrylic every frame
/// measured about as much CPU again as the whole flyout, and a ScrollViewer glide also works the UI thread every frame.
/// </remarks>
internal sealed class FlyoutLyricsList
{
    private const float DimOpacity = 0.38f;

    // The line before the current one stays in view when it takes up to this much room (one row and the spacing).
    private const double MaxContext = 32;

    private const double WheelStep = 0.5;

    private static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan ManualScrollPause = TimeSpan.FromSeconds(4);

    private readonly FrameworkElement _viewport;
    private readonly StackPanel _host;
    private readonly Style _lineStyle;
    private readonly Visual _hostVisual;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _ease;
    private readonly DispatcherQueueTimer _resume;
    private IReadOnlyList<LyricLineViewModel>? _lines;
    private int _current = -1;
    private double _offset;
    private DateTime _followPausedUntil;

    /// <param name="viewport">The visible area; it clips the lines.</param>
    /// <param name="host">The lines' panel, on a canvas inside <paramref name="viewport"/> so it keeps its full height.</param>
    public FlyoutLyricsList(FrameworkElement viewport, StackPanel host, Style lineStyle)
    {
        _viewport = viewport;
        _host = host;
        _lineStyle = lineStyle;
        ElementCompositionPreview.SetIsTranslationEnabled(host, true);
        _hostVisual = ElementCompositionPreview.GetElementVisual(host);
        _compositor = _hostVisual.Compositor;
        _ease = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.25f, 0.1f), new Vector2(0.25f, 1f));
        ElementCompositionPreview.GetElementVisual(viewport).Clip = _compositor.CreateInsetClip();

        _resume = host.DispatcherQueue.CreateTimer();
        _resume.Interval = ManualScrollPause;
        _resume.IsRepeating = false;
        _resume.Tick += (_, _) =>
        {
            _followPausedUntil = default;
            Follow();
        };

        viewport.PointerWheelChanged += OnPointerWheelChanged;
        viewport.SizeChanged += (_, e) =>
        {
            host.Width = e.NewSize.Width;
            host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Follow);
        };
    }

    /// <summary>Line changes fade (Windows animation effects on).</summary>
    public bool MotionEnabled { get; set; } = true;

    /// <summary>Lays out <paramref name="lines"/> (unless they are already shown) and jumps to <paramref name="current"/>.</summary>
    public void SetLines(IReadOnlyList<LyricLineViewModel> lines, int current)
    {
        if (!ReferenceEquals(lines, _lines))
        {
            _lines = lines;
            _host.Children.Clear();
            foreach (var line in lines)
            {
                var text = new TextBlock
                {
                    Style = _lineStyle,
                    Text = line.IsInstrumental ? CompactLyricsViewModel.PauseText : line.Text,
                };
                ElementCompositionPreview.GetElementVisual(text).Opacity = DimOpacity;
                _host.Children.Add(text);
            }

            _current = -1;
        }

        _followPausedUntil = default;
        _resume.Stop();
        SetCurrent(current, animate: false);

        // The lines get their positions in the next layout pass.
        _host.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, Follow);
    }

    /// <summary>Lights up line <paramref name="index"/> (-1: none yet) and brings it to the top.</summary>
    public void SetCurrent(int index, bool animate)
    {
        animate &= MotionEnabled;
        if (index != _current)
        {
            SetOpacity(_current, DimOpacity, animate);
            _current = index;
            SetOpacity(index, 1f, animate);
        }

        Follow();
    }

    /// <summary>The flyout closed: the next opening starts at the current line again.</summary>
    public void Reset()
    {
        _followPausedUntil = default;
        _resume.Stop();
    }

    private void SetOpacity(int index, float opacity, bool animate)
    {
        if (index < 0 || index >= _host.Children.Count)
        {
            return;
        }

        var visual = ElementCompositionPreview.GetElementVisual(_host.Children[index]);
        visual.StopAnimation("Opacity");
        if (!animate)
        {
            visual.Opacity = opacity;
            return;
        }

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, opacity, _ease);
        fade.Duration = Fade;
        visual.StartAnimation("Opacity", fade);
    }

    private void Follow()
    {
        if (DateTime.UtcNow < _followPausedUntil)
        {
            return;
        }

        var index = _current;
        double target = 0;
        if (index > 0 && index < _host.Children.Count)
        {
            // The line before stays in view when it is short (one row); a longer one would only show its last words.
            var top = TopOf(index);
            var previousTop = TopOf(index - 1);
            target = top - previousTop <= MaxContext ? previousTop : top;
        }

        MoveTo(target);
    }

    private void MoveTo(double offset)
    {
        offset = Math.Clamp(offset, 0, MaxOffset());
        if (Math.Abs(offset - _offset) >= 0.5)
        {
            _offset = offset;
            _hostVisual.Properties.InsertVector3("Translation", new Vector3(0, (float)-offset, 0));
        }
    }

    // The last line can reach the top.
    private double MaxOffset() => _host.Children.Count == 0 ? 0 : Math.Max(0, TopOf(_host.Children.Count - 1));

    private double TopOf(int index) => _host.Children[index].TransformToVisual(_host).TransformPoint(default).Y;

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (_host.Children.Count == 0)
        {
            return;
        }

        var delta = e.GetCurrentPoint(_viewport).Properties.MouseWheelDelta;
        MoveTo(_offset - (delta * WheelStep));
        _followPausedUntil = DateTime.UtcNow + ManualScrollPause;
        _resume.Stop();
        _resume.Start();
        e.Handled = true;
    }
}
