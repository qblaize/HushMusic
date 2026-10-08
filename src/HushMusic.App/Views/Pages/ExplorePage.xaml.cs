using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class ExplorePage : Page
{
    // Below this the chart rows fall back to a single column.
    private const double TwoColumnMinWidth = 860;

    public ExplorePage()
    {
        InitializeComponent();
    }

    public ExploreViewModel ViewModel { get; } = App.GetService<ExploreViewModel>();

    /// <summary>Placeholder tiles for the first load.</summary>
    internal int[] SkeletonTiles { get; } = [.. Enumerable.Range(0, 8)];

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

    private void OnChartTrackClick(object sender, ItemClickEventArgs e) => ViewModel.PlayChartTrackCommand.Execute(e.ClickedItem);

    private void OnMoodClick(object sender, RoutedEventArgs e) => ViewModel.OpenMoodCommand.Execute(((ExploreMoodTile)sender).Category);

    private void OnChartGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ChartGrid.ItemsPanelRoot is ItemsWrapGrid panel && e.NewSize.Width > 0)
        {
            var columns = e.NewSize.Width >= TwoColumnMinWidth ? 2 : 1;
            panel.MaximumRowsOrColumns = columns;
            panel.ItemWidth = Math.Floor(e.NewSize.Width / columns);
        }
    }
}
