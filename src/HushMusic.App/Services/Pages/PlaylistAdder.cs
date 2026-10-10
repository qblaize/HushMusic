using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// Adds songs to one of the user's existing playlists. YouTube Music refuses the whole add when a song is already in the
/// playlist; the user is then asked whether to add it anyway or, for several songs, to skip the ones already there.
/// </summary>
public interface IPlaylistAdder
{
    /// <summary>
    /// Returns the songs that were added: empty when the user cancelled, or when every song was already there (which shows a
    /// notice). Other failures throw; the caller reports them and shows its own success notice.
    /// </summary>
    Task<IReadOnlyList<Track>> AddAsync(Playlist playlist, IReadOnlyList<Track> tracks);
}

internal sealed class PlaylistAdder(
    IAccountActionsService account,
    IPlaylistDialogService dialogs,
    INotificationService notifications) : IPlaylistAdder
{
    public async Task<IReadOnlyList<Track>> AddAsync(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        List<Track> songs = [.. tracks.DistinctBy(t => t.VideoId, StringComparer.Ordinal)];
        if (songs.Count == 0)
        {
            return [];
        }

        List<string> videoIds = [.. songs.Select(t => t.VideoId)];
        try
        {
            await account.AddToPlaylistAsync(playlist.PlaylistId, videoIds);
            return songs;
        }
        catch (AlreadyInPlaylistException)
        {
            // Nothing was added.
        }

        switch (await dialogs.AskAboutDuplicatesAsync(playlist.Title, songs))
        {
            case DuplicateSongsChoice.AddAnyway:
                await account.AddToPlaylistAsync(playlist.PlaylistId, videoIds, allowDuplicates: true);
                return songs;

            case DuplicateSongsChoice.SkipDuplicates:
                var added = new HashSet<string>(await account.AddMissingToPlaylistAsync(playlist.PlaylistId, videoIds), StringComparer.Ordinal);
                if (added.Count == 0)
                {
                    notifications.ShowInfo("Nothing to add", $"All of these songs are already in {playlist.Title}.");
                }

                return [.. songs.Where(t => added.Contains(t.VideoId))];

            default:
                return [];
        }
    }
}
