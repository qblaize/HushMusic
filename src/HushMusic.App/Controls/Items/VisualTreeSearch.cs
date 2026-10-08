using Microsoft.UI.Xaml.Media;

namespace HushMusic.App.Controls.Items;

internal static class VisualTreeSearch
{
    public static T? FindAncestor<T>(DependencyObject? start)
        where T : DependencyObject
    {
        var current = start is null ? null : VisualTreeHelper.GetParent(start);
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    /// <summary>Walks up from <paramref name="start"/> (inclusive) and stops before <paramref name="stopAt"/>.</summary>
    public static T? FindAncestorOrSelf<T>(DependencyObject? start, DependencyObject? stopAt)
        where T : DependencyObject
    {
        var current = start;
        while (current is not null && !ReferenceEquals(current, stopAt))
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    public static T? FindDescendant<T>(DependencyObject root)
        where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var count = VisualTreeHelper.GetChildrenCount(current);
            for (var i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(current, i);
                if (child is T match)
                {
                    return match;
                }

                queue.Enqueue(child);
            }
        }

        return null;
    }
}
