using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Dialogs.Shell;

public sealed partial class CookieHeaderDialog : ContentDialog
{
    public CookieHeaderDialog(CookieHeaderDialogViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }

    public CookieHeaderDialogViewModel ViewModel { get; }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        // Keep the dialog open until sign-in finishes; stay open with the error if it fails.
        var deferral = args.GetDeferral();
        try
        {
            args.Cancel = !await ViewModel.SubmitAsync();
        }
        finally
        {
            deferral.Complete();
        }
    }
}
