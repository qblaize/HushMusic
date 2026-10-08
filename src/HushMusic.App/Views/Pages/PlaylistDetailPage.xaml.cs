using Microsoft.UI.Xaml.Navigation;
using HushMusic.App.Controls.Items;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Views.Pages;

public sealed partial class PlaylistDetailPage : Page
{
    private string? _playlistId;

    public PlaylistDetailPage()
    {
        InitializeComponent();
    }

    public PlaylistViewModel ViewModel { get; } = App.GetService<PlaylistViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        _playlistId = e.Parameter as string;
        _ = ViewModel.OnNavigatedToAsync(e.Parameter);
        if (e.NavigationMode != NavigationMode.Back)
        {
            CoverAnimation.TryStartForward(_playlistId, Header);
        }
    }

    protected override void OnNavigatingFrom(NavigatingCancelEventArgs e)
    {
        if (e.NavigationMode == NavigationMode.Back)
        {
            CoverAnimation.PrepareBack(_playlistId, Header);
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void OnTrackClick(object sender, ItemClickEventArgs e) => ViewModel.PlayTrackCommand.Execute(e.ClickedItem);
}
