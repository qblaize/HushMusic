using Microsoft.UI.Xaml.Controls.Primitives;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.Shell;

/// <summary>Rail account item: avatar + account flyout when signed in, "Sign in" otherwise.</summary>
public sealed partial class AccountButton : UserControl
{
    public AccountButton()
    {
        InitializeComponent();
    }

    public AccountViewModel ViewModel { get; } = App.GetService<AccountViewModel>();

    /// <summary>The rail item, so the shell can show its floating label.</summary>
    public RailButton RailItem => Item;

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.ShowAccount)
        {
            FlyoutBase.ShowAttachedFlyout(Item);
        }
        else if (ViewModel.IsInitialized || ViewModel.IsSigningIn)
        {
            // Signed out / expired: sign in. While signing in: brings the sign-in window to the front.
            ViewModel.SignInCommand.Execute(null);
        }
    }

    private void OnSignOutClick(object sender, RoutedEventArgs e) => AccountFlyout.Hide();
}
