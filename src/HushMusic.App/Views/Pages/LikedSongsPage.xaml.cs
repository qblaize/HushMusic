using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class LikedSongsPage : Page
{
    public LikedSongsPage()
    {
        InitializeComponent();
    }

    public LikedSongsViewModel ViewModel { get; } = App.GetService<LikedSongsViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (!TrackSelectionList.HandleClick(sender, e.ClickedItem))
        {
            ViewModel.PlayTrackCommand.Execute(e.ClickedItem);
        }
    }
}
