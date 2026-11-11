using Microsoft.Extensions.Logging;
using HushMusic.App.Controls.Items;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>"Pin to sidebar" / "Unpin from sidebar" from any menu. Methods never throw: failures show as toasts.</summary>
public interface ISidebarPinActions
{
    bool IsPinned(SidebarPin pin);

    /// <summary>Pins the item, or unpins it when it is already pinned.</summary>
    Task ToggleAsync(SidebarPin pin);

    Task UnpinAsync(SidebarPin pin);

    Task MoveAsync(SidebarPin pin, int offset);

    /// <summary>Keeps a pinned item's stored title and thumbnail current (its page loaded). Best effort.</summary>
    void Refresh(SidebarPin pin);
}

internal sealed class SidebarPinActions(ISidebarPins pins, INotificationService notifications, ILogger<SidebarPinActions> logger) : ISidebarPinActions
{
    public bool IsPinned(SidebarPin pin) => pins.Contains(pin.Kind, pin.Id);

    public async Task ToggleAsync(SidebarPin pin)
    {
        try
        {
            if (pins.Contains(pin.Kind, pin.Id))
            {
                await pins.UnpinAsync(pin.Kind, pin.Id);
            }
            else if (await pins.PinAsync(pin))
            {
                notifications.Show(new AppNotification(NotificationSeverity.Success, "Pinned to sidebar", pin.Title));
            }
            else
            {
                notifications.ShowInfo("The sidebar is full", $"You can pin up to {ISidebarPins.MaxCount} items. Unpin one to make room.");
            }
        }
        catch (Exception ex)
        {
            notifications.ShowError("Couldn't change your pinned items", ex);
        }
    }

    public async Task UnpinAsync(SidebarPin pin)
    {
        try
        {
            await pins.UnpinAsync(pin.Kind, pin.Id);
        }
        catch (Exception ex)
        {
            notifications.ShowError("Couldn't unpin this", ex);
        }
    }

    public async Task MoveAsync(SidebarPin pin, int offset)
    {
        try
        {
            await pins.MoveAsync(pin.Kind, pin.Id, offset);
        }
        catch (Exception ex)
        {
            notifications.ShowError("Couldn't move this pin", ex);
        }
    }

    public void Refresh(SidebarPin pin)
    {
        if (!pins.Contains(pin.Kind, pin.Id))
        {
            return;
        }

        _ = RefreshAsync();

        async Task RefreshAsync()
        {
            try
            {
                await pins.RefreshAsync(pin);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not update the pinned {Kind}", pin.Kind);
            }
        }
    }
}

/// <summary>Sidebar pins for media items.</summary>
public static class SidebarPinItems
{
    // YouTube Music's "Liked music" playlist: it has its own sidebar item.
    private const string LikedMusicId = "LM";

    /// <summary>The pin for a playlist, album or artist; null for anything that can't be pinned (songs, mixes).</summary>
    public static SidebarPin? For(MediaItem? item) => item switch
    {
        Playlist { IsMix: false } playlist => ForPlaylist(playlist.PlaylistId, playlist.Title, ItemFormat.CardArt(playlist)),
        Album album when !string.IsNullOrWhiteSpace(album.BrowseId) && !string.IsNullOrWhiteSpace(album.Title) =>
            new SidebarPin { Kind = PinKind.Album, Id = album.BrowseId, Title = album.Title, ThumbnailUrl = ItemFormat.CardArt(album) },
        Artist artist => ForArtist(artist, null),
        _ => null,
    };

    /// <summary>
    /// An artist's pin. Artist pages only have a wide banner, which makes a poor round cover, so the square photo of the
    /// card the page was opened from (<paramref name="cardArt"/>) or a square thumbnail comes first.
    /// </summary>
    public static SidebarPin? ForArtist(Artist artist, string? cardArt) =>
        string.IsNullOrWhiteSpace(artist.BrowseId) || string.IsNullOrWhiteSpace(artist.Title)
            ? null
            : new SidebarPin { Kind = PinKind.Artist, Id = artist.BrowseId, Title = artist.Title, ThumbnailUrl = cardArt ?? SquareArt(artist) ?? ItemFormat.CardArt(artist) };

    /// <summary>The smallest square thumbnail of at least 120 pixels (else the largest square one); null when none is square.</summary>
    public static string? SquareArt(MediaItem? item)
    {
        var square = item?.Thumbnails.Where(t => t.Width > 0 && t.Width == t.Height).ToList() ?? [];
        return (square.Where(t => t.Width >= 120).MinBy(t => t.Width) ?? square.MaxBy(t => t.Width))?.Url;
    }

    public static SidebarPin? ForPlaylist(string? playlistId, string? title, string? thumbnailUrl)
    {
        if (string.IsNullOrWhiteSpace(playlistId) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var id = SidebarPin.NormalizeId(PinKind.Playlist, playlistId);
        return id == LikedMusicId ? null : new SidebarPin { Kind = PinKind.Playlist, Id = id, Title = title, ThumbnailUrl = thumbnailUrl };
    }

    public static string Label(bool isPinned) => isPinned ? "Unpin from sidebar" : "Pin to sidebar";

    public static string Glyph(bool isPinned) => isPinned ? "" : "";
}
