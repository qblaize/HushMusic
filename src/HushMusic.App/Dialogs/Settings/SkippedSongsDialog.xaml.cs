using HushMusic.App.Dialogs.Playlists;

namespace HushMusic.App.Dialogs.Settings;

/// <summary>The songs smart skip leaves out, each with "Forgive". The view model does the work.</summary>
public sealed partial class SkippedSongsDialog : ContentDialog
{
    private SkippedSongsDialog(SmartSkipSettingsViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DialogChrome.Apply(this);
    }

    public SmartSkipSettingsViewModel ViewModel { get; }

    public static async Task ShowAsync(SmartSkipSettingsViewModel viewModel) =>
        await App.GetService<IDialogService>().ShowAsync(new SkippedSongsDialog(viewModel));
}
