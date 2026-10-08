using System.Numerics;
using Microsoft.UI.Composition;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>The Cover Flow's one motion: a long, soft ease-out that always starts from where the value is right now.</summary>
internal static class CoverFlowMotion
{
    public static readonly TimeSpan Glide = TimeSpan.FromMilliseconds(560);

    /// <summary>
    /// Animates <paramref name="property"/> (a scalar of a property set, or a visual's Opacity) to
    /// <paramref name="value"/>, or sets it when <paramref name="duration"/> is null. Starting from the current, possibly
    /// still animating, value makes every change interruptible: a new target never makes anything jump.
    /// </summary>
    public static void Animate(CompositionObject target, string property, float value, TimeSpan? duration)
    {
        if (duration is not { } length)
        {
            target.StopAnimation(property);
            switch (target)
            {
                case CompositionPropertySet set:
                    set.InsertScalar(property, value);
                    break;
                case Visual visual when property == nameof(Visual.Opacity):
                    visual.Opacity = value;
                    break;
            }

            return;
        }

        var compositor = target.Compositor;
        var animation = compositor.CreateScalarKeyFrameAnimation();
        animation.Duration = length;
        animation.InsertExpressionKeyFrame(0f, "this.StartingValue");

        // Ease-out quint: fast off the mark (so a quick run of next-next-next keeps flowing), long and soft to rest.
        animation.InsertKeyFrame(1f, value, compositor.CreateCubicBezierEasingFunction(new Vector2(0.22f, 1f), new Vector2(0.36f, 1f)));
        target.StartAnimation(property, animation);
    }
}
