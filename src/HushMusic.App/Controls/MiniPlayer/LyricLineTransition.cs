using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;

namespace HushMusic.App.Controls.MiniPlayer;

/// <summary>
/// The mini player's change of lyric line: the old line rises and fades away while the new one rises into its place
/// and the next line fades in below. Composition animations for a third of a second; nothing runs between lines.
/// </summary>
internal sealed class LyricLineTransition
{
    private const float Rise = 14f;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan NextDelay = TimeSpan.FromMilliseconds(60);

    private readonly TextBlock _ghost;
    private readonly TextBlock _current;
    private readonly TextBlock _next;
    private readonly Compositor _compositor;
    private readonly CompositionEasingFunction _settle;
    private CompositionScopedBatch? _batch;

    /// <param name="ghost">Shows the old line while it leaves; must not take part in layout (e.g. on a canvas).</param>
    public LyricLineTransition(TextBlock ghost, TextBlock current, TextBlock next)
    {
        _ghost = ghost;
        _current = current;
        _next = next;
        foreach (var element in new UIElement[] { ghost, current, next })
        {
            ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        }

        _compositor = ElementCompositionPreview.GetElementVisual(current).Compositor;

        // cubic-bezier(0.2, 0, 0, 1): the soft settle the Now Playing lyrics use.
        _settle = _compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0f), new Vector2(0f, 1f));
        VisualOf(ghost).Opacity = 0;
    }

    /// <summary>The song moved on a line: <paramref name="previous"/> leaves, the lines now shown arrive.</summary>
    public void Play(string previous)
    {
        Stop();
        _ghost.Text = previous;
        _ghost.Width = _current.ActualWidth;

        var batch = _compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        Animate(_ghost, 0, -Rise, 1, 0, TimeSpan.Zero);
        Animate(_current, Rise, 0, 0, 1, TimeSpan.Zero);
        Animate(_next, Rise / 2, 0, 0, 1, NextDelay);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (ReferenceEquals(_batch, batch))
            {
                _batch = null;
                _ghost.Text = string.Empty;
            }
        };
        _batch = batch;
    }

    /// <summary>A jump (a seek, new lyrics, motion off): the lines just show.</summary>
    public void Snap()
    {
        Stop();
        _ghost.Text = string.Empty;
        VisualOf(_ghost).Opacity = 0;
        foreach (var element in new UIElement[] { _current, _next })
        {
            var visual = VisualOf(element);
            visual.Opacity = 1;
            visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
    }

    private static Visual VisualOf(UIElement element) => ElementCompositionPreview.GetElementVisual(element);

    private void Stop()
    {
        _batch = null;
        foreach (var element in new UIElement[] { _ghost, _current, _next })
        {
            var visual = VisualOf(element);
            visual.StopAnimation("Opacity");
            visual.StopAnimation("Translation");
        }
    }

    private void Animate(UIElement element, float fromY, float toY, float fromOpacity, float toOpacity, TimeSpan delay)
    {
        var visual = VisualOf(element);
        var move = _compositor.CreateVector3KeyFrameAnimation();
        move.InsertKeyFrame(0, new Vector3(0, fromY, 0));
        move.InsertKeyFrame(1, new Vector3(0, toY, 0), _settle);
        move.Duration = Duration;
        move.DelayTime = delay;
        move.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        var fade = _compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0, fromOpacity);
        fade.InsertKeyFrame(1, toOpacity, _settle);
        fade.Duration = Duration;
        fade.DelayTime = delay;
        fade.DelayBehavior = AnimationDelayBehavior.SetInitialValueBeforeDelay;

        visual.StartAnimation("Translation", move);
        visual.StartAnimation("Opacity", fade);
    }
}
