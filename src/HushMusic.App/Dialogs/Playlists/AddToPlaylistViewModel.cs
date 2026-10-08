using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using HushMusic.App.Services.Pages;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Dialogs.Playlists;

/// <summary>Lists the user's playlists for the "Add to playlist" dialog and records the choice.</summary>
public sealed partial class AddToPlaylistViewModel(ILibraryApi library) : ObservableObject
{
    // Auto playlists that cannot be edited through browse/edit_playlist.
    private static readonly HashSet<string> ReadOnlyPlaylistIds = new(StringComparer.Ordinal) { "LM", "SE" };

    private const int MaxPages = 20;

    public ObservableCollection<Playlist> Playlists { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public PlaylistChoice? Choice { get; private set; }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        IsLoading = true;
        try
        {
            string? continuation = null;
            var pages = 0;
            do
            {
                var page = await library.GetPlaylistsAsync(continuation, cancellationToken);
                foreach (var playlist in page.Items.Where(p => !ReadOnlyPlaylistIds.Contains(p.PlaylistId)))
                {
                    Playlists.Add(playlist);
                }

                continuation = page.Continuation;
            }
            while (continuation is not null && ++pages < MaxPages);

            if (Playlists.Count == 0)
            {
                Message = "You don't have any playlists yet. Create one with “New playlist”.";
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (AuthRequiredException ex)
        {
            Message = ex.Message;
        }
        catch (Exception ex)
        {
            Message = $"Couldn't load your playlists. {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Pick(Playlist playlist) => Choice = new PlaylistChoice(playlist);

    public void PickNew() => Choice = new PlaylistChoice(null);
}
