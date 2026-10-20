using Microsoft.UI.Xaml.Media.Animation;

namespace HushMusic.App.Controls.Items;

/// <summary>Which item transitions a list keeps (<see cref="ItemMotion"/>).</summary>
public enum ItemMotionMode
{
    /// <summary>The control's own: for ListView and GridView, entrance, content, add/delete and reorder.</summary>
    Default,

    /// <summary>Items appear and go without moving.</summary>
    None,

    /// <summary>Only the glide of rows dragged (or moved) to a new place.</summary>
    Reorder,
}

/// <summary>
/// <c>items:ItemMotion.Mode="None"</c> on a page's ListView or GridView. A page opens with one motion: the frame's slide,
/// then a fade (<see cref="Reveal"/>) when the content replaces the skeleton. The stock item transitions slid every item up
/// a second time when it first appeared (a list filled after the page had already slid in, each carousel of a shelf, all
/// the rows of a refreshed feed), so content looked as if it loaded twice. "Reorder" keeps only the glide, for lists whose
/// rows can be dragged into a new order.
/// </summary>
public static class ItemMotion
{
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(ItemMotionMode), typeof(ItemMotion), new PropertyMetadata(ItemMotionMode.Default, OnModeChanged));

    public static ItemMotionMode GetMode(DependencyObject element) => (ItemMotionMode)element.GetValue(ModeProperty);

    public static void SetMode(DependencyObject element, ItemMotionMode value) => element.SetValue(ModeProperty, value);

    private static void OnModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl list)
        {
            return;
        }

        // A local value, so it wins over the transitions the default style sets; every list gets a collection of its own.
        switch ((ItemMotionMode)e.NewValue)
        {
            case ItemMotionMode.None:
                list.ItemContainerTransitions = [];
                break;
            case ItemMotionMode.Reorder:
                list.ItemContainerTransitions = [new ReorderThemeTransition()];
                break;
            default:
                list.ClearValue(ItemsControl.ItemContainerTransitionsProperty);
                break;
        }
    }
}
