using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class ExploreCategoryPage : Page
{
    // Below this the chart artists fall back to a single column.
    private const double TwoColumnMinWidth = 860;

    public ExploreCategoryPage()
    {
        InitializeComponent();
    }

    public ExploreCategoryViewModel ViewModel { get; } = App.GetService<ExploreCategoryViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenItemCommand.Execute(e.ClickedItem);

    private void OnGridItemClick(object sender, ItemClickEventArgs e)
    {
        CoverAnimation.PrepareFrom((ListViewBase)sender, e.ClickedItem);
        ViewModel.OpenItemCommand.Execute(e.ClickedItem);
    }

    private void OnArtistClick(object sender, ItemClickEventArgs e) => ViewModel.OpenArtistCommand.Execute(e.ClickedItem);

    private void OnMoodClick(object sender, RoutedEventArgs e) => ViewModel.OpenMoodCommand.Execute(((ExploreMoodTile)sender).Category);

    private void OnArtistGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (ArtistGrid.ItemsPanelRoot is ItemsWrapGrid panel && e.NewSize.Width > 0)
        {
            var columns = e.NewSize.Width >= TwoColumnMinWidth ? 2 : 1;
            panel.MaximumRowsOrColumns = columns;
            panel.ItemWidth = Math.Floor(e.NewSize.Width / columns);
        }
    }
}
