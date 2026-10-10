using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

public enum PlaylistDropState
{
    /// <summary>Ownership is being checked; ask again on the next DragOver.</summary>
    Unknown,
    Accepts,
    Rejects,
}

/// <summary>
/// Songs dropped on a playlist card are added to it, but only playlists the user owns accept them. The library listing
/// doesn't say who owns a playlist, so the playlist page (its editable header) is read once, the first time a drag
/// passes over the card, and remembered for the session.
/// </summary>
public interface IPlaylistDropTarget
{
    /// <summary>Cheap enough for DragOver. Starts the ownership check when it isn't known yet.</summary>
    PlaylistDropState GetState(Playlist playlist);

    /// <summary>Adds the songs and shows "Added to …", asking first when some are already in the playlist. Never throws.</summary>
    Task AddAsync(Playlist playlist, IReadOnlyList<Track> tracks);
}

internal sealed class PlaylistDropTarget : IPlaylistDropTarget
{
    // Liked music and Episodes for later can't be edited through browse/edit_playlist.
    private static readonly HashSet<string> ReadOnlyPlaylistIds = new(StringComparer.Ordinal) { "LM", "SE" };

    private readonly IBrowseApi _browse;
    private readonly IAccountActionsService _account;
    private readonly IPlaylistAdder _adder;
    private readonly IAuthService _auth;
    private readonly INotificationService _notifications;
    private readonly ILogger<PlaylistDropTarget> _logger;
    private readonly ConcurrentDictionary<string, Task<bool>> _owned = new(StringComparer.Ordinal);

    public PlaylistDropTarget(
        IBrowseApi browse,
        IAccountActionsService account,
        IPlaylistAdder adder,
        IAuthService auth,
        INotificationService notifications,
        ILogger<PlaylistDropTarget> logger)
    {
        _browse = browse;
        _account = account;
        _adder = adder;
        _auth = auth;
        _notifications = notifications;
        _logger = logger;

        // Another account (or none) owns different playlists.
        _auth.StatusChanged += (_, _) => _owned.Clear();
        _account.PlaylistChanged += (_, e) =>
        {
            switch (e.Kind)
            {
                case PlaylistChangeKind.Created:
                    _owned[e.PlaylistId] = Task.FromResult(true);
                    break;
                case PlaylistChangeKind.Deleted:
                    _owned.TryRemove(e.PlaylistId, out Task<bool>? _);
                    break;
            }
        };
    }

    public PlaylistDropState GetState(Playlist playlist)
    {
        if (_auth.Status != AuthStatus.SignedIn || playlist.IsMix || ReadOnlyPlaylistIds.Contains(playlist.PlaylistId))
        {
            return PlaylistDropState.Rejects;
        }

        var check = _owned.GetOrAdd(playlist.PlaylistId, CheckOwnedAsync);
        if (!check.IsCompleted)
        {
            return PlaylistDropState.Unknown;
        }

        return check.IsCompletedSuccessfully && check.Result ? PlaylistDropState.Accepts : PlaylistDropState.Rejects;
    }

    public async Task AddAsync(Playlist playlist, IReadOnlyList<Track> tracks)
    {
        var songs = tracks.Where(t => t.IsAvailable).ToList();
        if (songs.Count == 0)
        {
            return;
        }

        try
        {
            var added = await _adder.AddAsync(playlist, songs);
            if (added.Count > 0)
            {
                var what = added.Count == 1 ? added[0].Title : $"{added.Count} songs";
                _notifications.Show(new AppNotification(NotificationSeverity.Success, $"Added to {playlist.Title}", what));
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Couldn't add to {playlist.Title}", ex);
        }
    }

    private async Task<bool> CheckOwnedAsync(string playlistId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var page = await _browse.GetPlaylistAsync(playlistId, timeout.Token);
            return page.IsOwned;
        }
        catch (Exception ex)
        {
            // Not cached: the next drag over this card asks again.
            _logger.LogDebug(ex, "Could not check whether playlist {PlaylistId} is owned", playlistId);
            _owned.TryRemove(playlistId, out Task<bool>? _);
            return false;
        }
    }
}
