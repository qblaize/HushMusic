using HushMusic.App.Dialogs.Playlists;

namespace HushMusic.App.Services.Pages;

/// <summary>A yes/no question before something that can't be undone.</summary>
public interface IConfirmDialogService
{
    /// <summary>True when the user chose <paramref name="confirmText"/>.</summary>
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = true);
}

internal sealed class ConfirmDialogService(IDialogService dialogs) : IConfirmDialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string confirmText, bool isDestructive = true)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock
            {
                Style = (Style)Application.Current.Resources["SubheadStyle"],
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                MaxWidth = 380,
            },
            PrimaryButtonText = confirmText,
            CloseButtonText = "Cancel",
        };
        DialogChrome.Apply(dialog);
        if (isDestructive)
        {
            dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DangerPillButtonStyle"];
        }

        return await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }
}
