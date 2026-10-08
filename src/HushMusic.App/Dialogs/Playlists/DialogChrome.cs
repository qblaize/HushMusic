using Microsoft.UI;
using Microsoft.UI.Xaml.Media;

namespace HushMusic.App.Dialogs.Playlists;

/// <summary>
/// Flat, rounded, pill-button look for the playlist dialogs (AppDialogStyle in PageResources.xaml), plus the
/// lightweight-styling keys that flatten the stock two-tone layout. Scoped to each dialog, so other dialogs are untouched.
/// Theme-neutral: the colours come from AppDialogStyle's {ThemeResource} setters; IDialogService sets the dialog's theme.
/// </summary>
internal static class DialogChrome
{
    public static void Apply(ContentDialog dialog)
    {
        dialog.Style = (Style)Application.Current.Resources["AppDialogStyle"];

        // The stock dialog paints its content area a shade lighter than the button area, with a divider between them.
        var transparent = new SolidColorBrush(Colors.Transparent);
        dialog.Resources["ContentDialogTopOverlay"] = transparent;
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = transparent;

        // No default button: ContentDialog swaps the default button's style for the stock AccentButtonStyle,
        // which would undo the pill styles. Dialogs that need Enter handle it themselves.
        dialog.DefaultButton = ContentDialogButton.None;
    }
}
