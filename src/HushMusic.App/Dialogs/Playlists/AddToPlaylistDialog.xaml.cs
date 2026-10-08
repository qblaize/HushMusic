using HushMusic.Core.Models;

namespace HushMusic.App.Dialogs.Playlists;

public sealed partial class AddToPlaylistDialog : ContentDialog
{
    public AddToPlaylistDialog(AddToPlaylistViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DialogChrome.Apply(this);
    }

    public AddToPlaylistViewModel ViewModel { get; }

    private void OnPlaylistClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Playlist playlist)
        {
            ViewModel.Pick(playlist);
            Hide();
        }
    }

    private void OnNewPlaylistClick(object sender, RoutedEventArgs e)
    {
        ViewModel.PickNew();
        Hide();
    }
}
