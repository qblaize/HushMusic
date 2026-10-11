using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();
    }

    public HomeViewModel ViewModel { get; } = App.GetService<HomeViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _ = ViewModel.OnNavigatedToAsync(e.Parameter, e.NavigationMode == NavigationMode.Back);
        if (ViewModel.IsFreshVisit)
        {
            Scroller.ChangeView(null, 0, null, disableAnimation: true);
        }

        if (e.NavigationMode == NavigationMode.Back)
        {
            // Lands only when the card is still on screen (not after a fresh reload).
            CoverAnimation.TryStartBack(this);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenItemCommand.Execute(e.ClickedItem);

    // Anywhere on the "Fresh for you" card but its buttons opens the mix.
    private void OnFreshMixTapped(object sender, TappedRoutedEventArgs e)
    {
        for (var element = e.OriginalSource as DependencyObject; element is not null && !ReferenceEquals(element, sender); element = VisualTreeHelper.GetParent(element))
        {
            if (element is ButtonBase)
            {
                return;
            }
        }

        ViewModel.FreshMix.OpenCommand.Execute(null);
    }

    private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) => LoadMoreIfNearEnd();

    // Also checked when the shelves or the window change size: a first page that doesn't fill the window
    // never scrolls. (The repeater, not the StackPanel: the ScrollViewer stretches short content to the viewport.)
    private void OnLayoutSizeChanged(object sender, SizeChangedEventArgs e) => LoadMoreIfNearEnd();

    private void LoadMoreIfNearEnd()
    {
        if (Scroller.ScrollableHeight - Scroller.VerticalOffset < Scroller.ViewportHeight)
        {
            ViewModel.LoadMoreCommand.Execute(null);
        }
    }
}
