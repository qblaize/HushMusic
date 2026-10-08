using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class AlbumDetailPage : Page
{
    private string? _browseId;

    public AlbumDetailPage()
    {
        InitializeComponent();
    }

    public AlbumViewModel ViewModel { get; } = App.GetService<AlbumViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _browseId = e.Parameter as string;
        _ = ViewModel.OnNavigatedToAsync(e.Parameter);
        if (e.NavigationMode != NavigationMode.Back)
        {
            CoverAnimation.TryStartForward(_browseId, Header);
        }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        if (e.NavigationMode == NavigationMode.Back)
        {
            CoverAnimation.PrepareBack(_browseId, Header);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (!TrackSelectionList.HandleClick(sender, e.ClickedItem))
        {
            ViewModel.PlayTrackCommand.Execute(e.ClickedItem);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e) => ViewModel.OpenItemCommand.Execute(e.ClickedItem);
}
