using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// Hands the item a detail page was opened from (a card, a row, a search suggestion) to that page, so its header can
/// show the title and the already-cached artwork while the full page loads. UI thread only.
/// </summary>
public interface INavigationPreviews
{
    /// <summary>Call right before navigating to the item's page.</summary>
    void Remember(MediaItem item);

    /// <summary>The remembered item when it is a <typeparamref name="T"/> with this page id; it is handed out once.</summary>
    T? Take<T>(string? id)
        where T : MediaItem;
}

internal sealed class NavigationPreviews : INavigationPreviews
{
    private MediaItem? _item;
    private string? _id;

    public void Remember(MediaItem item)
    {
        _item = item;
        _id = IdOf(item);
    }

    public T? Take<T>(string? id)
        where T : MediaItem
    {
        var match = id is not null && _item is T item && string.Equals(_id, id, StringComparison.Ordinal) ? item : null;
        _item = null;
        _id = null;
        return match;
    }

    /// <summary>The navigation parameter of the item's page.</summary>
    internal static string? IdOf(object? item) => item switch
    {
        Album album => album.BrowseId,
        Playlist { IsMix: false } playlist => playlist.PlaylistId,
        Artist artist => artist.BrowseId,
        _ => null,
    };
}
