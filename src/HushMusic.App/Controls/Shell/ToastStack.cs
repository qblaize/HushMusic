using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using HushMusic.App.Controls.Items;

namespace HushMusic.App.Controls.Shell;

/// <summary>
/// The in-app toasts, bottom right (when they come and go is decided by <see cref="Core.Services.NotificationStack"/>).
/// A new toast slides in from the right as it fades in, a closed one slides back out as it fades, and the others glide to
/// their new places; all of it is instant when Windows' animation effects are off. Use with a <see cref="ToastPanel"/>.
/// <see cref="HeldChanged"/> reports the pointer resting on the toasts, which pauses their timers.
/// </summary>
public sealed partial class ToastStack : ItemsControl
{
    private const float SlideDistance = 56;
    private static readonly TimeSpan ShowDuration = TimeSpan.FromMilliseconds(320);
    private static readonly TimeSpan HideDuration = TimeSpan.FromMilliseconds(200);

    private CompositionAnimationGroup? _show;
    private CompositionAnimationGroup? _hide;
    private bool _isHeld;

    public ToastStack()
    {
        IsTabStop = false;
        if (Skeleton.AnimationsEnabled)
        {
            ItemContainerTransitions = [new RepositionThemeTransition { IsStaggeringEnabled = false }];
        }

        PointerEntered += (_, _) => SetHeld(true);
        PointerExited += OnPointerLeft;
        PointerCanceled += OnPointerLeft;
        PointerCaptureLost += OnPointerLeft;
    }

    /// <summary>True while the pointer is on a toast, false when it leaves them (or the last one goes).</summary>
    public event EventHandler<bool>? HeldChanged;

    protected override void PrepareContainerForItemOverride(DependencyObject element, object item)
    {
        base.PrepareContainerForItemOverride(element, item);
        if (element is UIElement container && Skeleton.AnimationsEnabled)
        {
            var visual = ElementCompositionPreview.GetElementVisual(container);
            ElementCompositionPreview.SetIsTranslationEnabled(container, true);
            ElementCompositionPreview.SetImplicitHideAnimation(container, _hide ??= CreateHide(visual.Compositor));

            // Started here rather than as an implicit show animation: the container is already in the tree when it is
            // prepared, so a show animation set now would never be triggered.
            visual.StartAnimationGroup(_show ??= CreateShow(visual.Compositor));
        }
    }

    protected override void OnItemsChanged(object e)
    {
        base.OnItemsChanged(e);

        // A toast closed under the pointer takes no PointerExited with it: an empty stack can't be held.
        if (Items.Count == 0)
        {
            SetHeld(false);
        }
    }

    private static CompositionAnimationGroup CreateShow(Compositor compositor)
    {
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.2f, 0.9f), new Vector2(0.3f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertKeyFrame(0, 0);
        fade.InsertKeyFrame(1, 1, easing);
        fade.Duration = ShowDuration;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertKeyFrame(0, new Vector3(SlideDistance, 0, 0));
        slide.InsertKeyFrame(1, Vector3.Zero, easing);
        slide.Duration = ShowDuration;
        var group = compositor.CreateAnimationGroup();
        group.Add(fade);
        group.Add(slide);
        return group;
    }

    private static CompositionAnimationGroup CreateHide(Compositor compositor)
    {
        var easing = compositor.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(1f, 1f));
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.Target = "Opacity";
        fade.InsertExpressionKeyFrame(0, "this.StartingValue");
        fade.InsertKeyFrame(1, 0, easing);
        fade.Duration = HideDuration;
        var slide = compositor.CreateVector3KeyFrameAnimation();
        slide.Target = "Translation";
        slide.InsertExpressionKeyFrame(0, "this.StartingValue");
        slide.InsertKeyFrame(1, new Vector3(SlideDistance, 0, 0), easing);
        slide.Duration = HideDuration;
        var group = compositor.CreateAnimationGroup();
        group.Add(fade);
        group.Add(slide);
        return group;
    }

    // Leaving one toast for the next raises PointerExited too: still held while the pointer is on any of them.
    private void OnPointerLeft(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this).Position;
        SetHeld(ItemsPanelRoot is { } panel && IsOverChild(panel, point));
    }

    private bool IsOverChild(Panel panel, Point point)
    {
        foreach (var child in panel.Children)
        {
            var size = child.ActualSize;
            var bounds = child.TransformToVisual(this).TransformBounds(new Rect(0, 0, size.X, size.Y));
            if (bounds.Contains(point))
            {
                return true;
            }
        }

        return false;
    }

    private void SetHeld(bool held)
    {
        if (held != _isHeld)
        {
            _isHeld = held;
            HeldChanged?.Invoke(this, held);
        }
    }
}

/// <summary>
/// Stacks the toasts upward from its bottom edge, newest at the bottom. The panel itself takes no height, so a toast's
/// place in it only changes when a toast below it comes or goes (and then it glides there): resizing the window moves
/// the whole stack, never one toast within it.
/// </summary>
public sealed partial class ToastPanel : Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var width = 0.0;
        foreach (var child in Children)
        {
            child.Measure(new Size(availableSize.Width, double.PositiveInfinity));
            width = Math.Max(width, child.DesiredSize.Width);
        }

        return new Size(double.IsInfinity(availableSize.Width) ? width : availableSize.Width, 0);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bottom = finalSize.Height;
        for (var i = Children.Count - 1; i >= 0; i--)
        {
            var child = Children[i];
            var height = child.DesiredSize.Height;
            bottom -= height;
            child.Arrange(new Rect(0, bottom, finalSize.Width, height));
        }

        return finalSize;
    }
}
