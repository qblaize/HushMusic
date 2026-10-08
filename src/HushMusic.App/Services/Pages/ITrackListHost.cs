using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// Implemented by page view models that show tracks in a play context (album, playlist, liked songs...).
/// Track menus find it through the <c>TrackMenu.Host</c> attached property on an ancestor element.
/// </summary>
public interface ITrackListHost
{
    /// <summary>Plays the page's list starting at <paramref name="track"/>.</summary>
    Task PlayFromTrackAsync(Track track);

    bool CanRemoveFromPlaylist(Track track);

    Task RemoveFromPlaylistAsync(Track track);
}
