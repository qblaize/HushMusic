using Microsoft.UI.Xaml.Navigation;
using Windows.ApplicationModel.DataTransfer;
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

    private void OnTrackClick(object sender, ItemClickEventArgs e)
    {
        if (!TrackSelectionList.HandleClick(sender, e.ClickedItem))
        {
            ViewModel.PlayTrackCommand.Execute(e.ClickedItem);
        }
    }

    // A reorder ends as a Move inside the list; drops on the queue or a playlist are copies.
    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        ViewModel.BeginReorder(e.Items.Count == 1 ? e.Items[0] : null);

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.CompleteReorder(args.DropResult == DataPackageOperation.Move);
}
