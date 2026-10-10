using HushMusic.App.Dialogs.Playlists;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

public sealed record PlaylistDetails(string Title, string? Description, PrivacyStatus Privacy);

/// <summary>The user's pick in the "Add to playlist" dialog. A null <see cref="Playlist"/> means "New playlist…".</summary>
public sealed record PlaylistChoice(Playlist? Playlist);

/// <summary>The answer to "already in this playlist". <see cref="SkipDuplicates"/> is only offered for several songs.</summary>
public enum DuplicateSongsChoice
{
    Cancel,
    AddAnyway,
    SkipDuplicates,
}

/// <summary>Playlist dialogs. Only asks the user; the caller performs the account action.</summary>
public interface IPlaylistDialogService
{
    Task<PlaylistDetails?> PromptCreateAsync();

    Task<PlaylistDetails?> PromptEditAsync(PlaylistDetails current);

    Task<bool> ConfirmDeleteAsync(string playlistTitle);

    /// <summary>Lists the user's playlists; returns null when cancelled.</summary>
    Task<PlaylistChoice?> PickPlaylistAsync();

    /// <summary>
    /// Asks what to do after YouTube Music refused to add <paramref name="tracks"/> because some are already in the playlist.
    /// One song can be added anyway; for several, skipping the ones already there is offered too.
    /// </summary>
    Task<DuplicateSongsChoice> AskAboutDuplicatesAsync(string playlistTitle, IReadOnlyList<Track> tracks);
}

internal sealed class PlaylistDialogService(IDialogService dialogs, ILibraryApi library) : IPlaylistDialogService
{
    public Task<PlaylistDetails?> PromptCreateAsync() => PromptAsync(null);

    public Task<PlaylistDetails?> PromptEditAsync(PlaylistDetails current) => PromptAsync(current);

    public async Task<bool> ConfirmDeleteAsync(string playlistTitle)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete playlist?",
            Content = new TextBlock
            {
                Style = (Style)Application.Current.Resources["SubheadStyle"],
                Text = $"“{playlistTitle}” will be deleted from your account. This can't be undone.",
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                MaxWidth = 380,
            },
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        DialogChrome.Apply(dialog);
        dialog.PrimaryButtonStyle = (Style)Application.Current.Resources["DangerPillButtonStyle"];
        return await dialogs.ShowAsync(dialog) == ContentDialogResult.Primary;
    }

    public async Task<PlaylistChoice?> PickPlaylistAsync()
    {
        var viewModel = new AddToPlaylistViewModel(library);
        using var cts = new CancellationTokenSource();
        _ = viewModel.LoadAsync(cts.Token);
        try
        {
            await dialogs.ShowAsync(new AddToPlaylistDialog(viewModel));
        }
        finally
        {
            cts.Cancel();
        }

        return viewModel.Choice;
    }

    public async Task<DuplicateSongsChoice> AskAboutDuplicatesAsync(string playlistTitle, IReadOnlyList<Track> tracks)
    {
        var single = tracks.Count == 1;
        var dialog = new ContentDialog
        {
            Title = single ? $"Already in {playlistTitle}" : $"Some songs are already in {playlistTitle}",
            Content = new TextBlock
            {
                Style = (Style)Application.Current.Resources["SubheadStyle"],
                Text = single
                    ? $"“{tracks[0].Title}” is already in this playlist."
                    : $"Add all {tracks.Count} songs anyway, or only the ones that aren't in this playlist yet?",
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.None,
                MaxWidth = 380,

                // The title names the playlist and can make the dialog wider than the text: keep the text under it.
                HorizontalAlignment = HorizontalAlignment.Left,
            },
            PrimaryButtonText = "Add anyway",
            SecondaryButtonText = single ? string.Empty : "Skip duplicates",
            CloseButtonText = "Cancel",
        };
        DialogChrome.Apply(dialog);

        return await dialogs.ShowAsync(dialog) switch
        {
            ContentDialogResult.Primary => DuplicateSongsChoice.AddAnyway,
            ContentDialogResult.Secondary => DuplicateSongsChoice.SkipDuplicates,
            _ => DuplicateSongsChoice.Cancel,
        };
    }

    private async Task<PlaylistDetails?> PromptAsync(PlaylistDetails? current)
    {
        var viewModel = new PlaylistEditorViewModel(current);
        var result = await dialogs.ShowAsync(new PlaylistEditorDialog(viewModel));
        var accepted = result == ContentDialogResult.Primary || viewModel.IsSubmitted;
        return accepted && viewModel.IsValid ? viewModel.ToDetails() : null;
    }
}
