using HushMusic.App.Dialogs.Playlists;

namespace HushMusic.App.Dialogs.Settings;

/// <summary>The artists Hush doesn't add by itself, each with "Unblock". The view model does the work.</summary>
public sealed partial class BlockedArtistsDialog : ContentDialog
{
    private BlockedArtistsDialog(BlockedArtistsSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DialogChrome.Apply(this);
    }

    public BlockedArtistsSettingsViewModel ViewModel { get; }

    public static async Task ShowAsync(BlockedArtistsSettingsViewModel viewModel) =>
        await App.GetService<IDialogService>().ShowAsync(new BlockedArtistsDialog(viewModel));
}
