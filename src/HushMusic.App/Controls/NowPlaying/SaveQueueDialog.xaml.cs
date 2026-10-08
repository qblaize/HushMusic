using Microsoft.UI.Xaml.Input;
using Windows.System;
using HushMusic.App.Dialogs.Playlists;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>Asks for the name of the playlist Up next is saved as. Only asks; the caller creates the playlist.</summary>
public sealed partial class SaveQueueDialog : ContentDialog
{
    private bool _submitted;

    private SaveQueueDialog(string suggestedName, int songCount)
    {
        InitializeComponent();
        DialogChrome.Apply(this);
        NameBox.Text = suggestedName;
        NoteText.Text = songCount == 1
            ? "A private playlist with the song in this queue."
            : $"A private playlist with the {songCount} songs in this queue, in this order.";
        IsPrimaryButtonEnabled = HasName;
        Opened += (_, _) =>
        {
            NameBox.Focus(FocusState.Programmatic);
            NameBox.SelectAll();
        };
    }

    private bool HasName => !string.IsNullOrWhiteSpace(NameBox.Text);

    /// <summary>The chosen name, or null when the dialog was cancelled.</summary>
    public static async Task<string?> AskAsync(string suggestedName, int songCount)
    {
        var dialog = new SaveQueueDialog(suggestedName, songCount);
        var result = await App.GetService<IDialogService>().ShowAsync(dialog);
        return (result == ContentDialogResult.Primary || dialog._submitted) && dialog.HasName ? dialog.NameBox.Text.Trim() : null;
    }

    private void OnNameChanged(object sender, TextChangedEventArgs e) => IsPrimaryButtonEnabled = HasName;

    // Enter saves, as the stock default button would (the dialog has none, see DialogChrome).
    private void OnNameKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && HasName)
        {
            e.Handled = true;
            _submitted = true;
            Hide();
        }
    }
}
