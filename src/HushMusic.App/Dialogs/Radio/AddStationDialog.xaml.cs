using Microsoft.UI.Xaml.Input;
using HushMusic.App.Dialogs.Playlists;
using Windows.System;

namespace HushMusic.App.Dialogs.Radio;

public sealed partial class AddStationDialog : ContentDialog
{
    private readonly CancellationTokenSource _closing = new();

    public AddStationDialog(AddStationViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        DialogChrome.Apply(this);
        IsPrimaryButtonEnabled = viewModel.CanAdd;
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AddStationViewModel.CanAdd))
            {
                IsPrimaryButtonEnabled = viewModel.CanAdd;
            }
        };
        Opened += (_, _) => LinkBox.Focus(FocusState.Programmatic);
        Closing += (_, _) => _closing.Cancel();
    }

    public AddStationViewModel ViewModel { get; }

    // The dialog stays open while the link is checked, and says why when it doesn't lead to audio.
    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.AddAsync(_closing.Token);
        }
        finally
        {
            deferral.Complete();
        }
    }

    // Enter adds, as the stock default button would (the dialog has none, see DialogChrome).
    private async void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || !ViewModel.CanAdd)
        {
            return;
        }

        e.Handled = true;
        if (await ViewModel.AddAsync(_closing.Token))
        {
            Hide();
        }
    }
}
