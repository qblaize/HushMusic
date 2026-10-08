using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HushMusic.App.Dialogs.Playlists;

public sealed partial class PlaylistEditorDialog : ContentDialog
{
    public PlaylistEditorDialog(PlaylistEditorViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DialogChrome.Apply(this);
        Title = viewModel.DialogTitle;
        PrimaryButtonText = viewModel.PrimaryText;
        IsPrimaryButtonEnabled = viewModel.IsValid;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlaylistEditorViewModel.IsValid))
            {
                IsPrimaryButtonEnabled = viewModel.IsValid;
            }
        };
        Opened += (_, _) => TitleBox.Focus(FocusState.Programmatic);
    }

    public PlaylistEditorViewModel ViewModel { get; }

    // Enter in the title submits, as the stock default button would (the dialog has none, see DialogChrome).
    private void OnTitleKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && ViewModel.IsValid)
        {
            e.Handled = true;
            ViewModel.Submit();
            Hide();
        }
    }
}
