using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class ArtistDetailPage : Page
{
    // Below this the top songs fall back to a single column of full rows.
    private const double TwoColumnMinWidth = 860;

    public ArtistDetailPage()
    {
        InitializeComponent();
    }

    public ArtistViewModel ViewModel { get; } = App.GetService<ArtistViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnTrackClick(object sender, ItemClickEventArgs e) => ViewModel.PlayTrackCommand.Execute(e.ClickedItem);

    private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenItemCommand.Execute(e.ClickedItem);

    private void OnTopSongsSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TopSongsGrid.ItemsPanelRoot is ItemsWrapGrid panel && e.NewSize.Width > 0)
        {
            var columns = e.NewSize.Width >= TwoColumnMinWidth ? 2 : 1;
            panel.MaximumRowsOrColumns = columns;
            panel.ItemWidth = Math.Floor(e.NewSize.Width / columns);
        }
    }
}
