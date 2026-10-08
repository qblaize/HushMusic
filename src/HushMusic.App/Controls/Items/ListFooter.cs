namespace HushMusic.App.Controls.Items;

/// <summary>
/// <c>items:ListFooter.FollowsItems="True"</c> on a ListView: a list shorter than the window stretches its items
/// presenter to the viewport, which parks the Footer ("1 song · 3 minutes") at the bottom of the window. Top-aligning
/// the presenter keeps the footer right under the last row.
/// </summary>
public static class ListFooter
{
    public static readonly DependencyProperty FollowsItemsProperty = DependencyProperty.RegisterAttached(
        "FollowsItems", typeof(bool), typeof(ListFooter), new PropertyMetadata(false, OnFollowsItemsChanged));

    public static bool GetFollowsItems(DependencyObject element) => (bool)element.GetValue(FollowsItemsProperty);

    public static void SetFollowsItems(DependencyObject element, bool value) => element.SetValue(FollowsItemsProperty, value);

    private static void OnFollowsItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ListViewBase list)
        {
            list.Loaded -= OnListLoaded;
            if (e.NewValue is true)
            {
                list.Loaded += OnListLoaded;
            }
        }
    }

    private static void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (VisualTreeSearch.FindDescendant<ItemsPresenter>((ListViewBase)sender) is { } presenter)
        {
            presenter.VerticalAlignment = VerticalAlignment.Top;
        }
    }
}
