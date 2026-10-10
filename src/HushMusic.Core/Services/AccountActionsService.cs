using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Services;

public sealed class AccountActionsService(IAccountApi api, IBrowseApi browse) : IAccountActionsService
{
    // Only stops a continuation that never ends; real playlists stay far below it.
    private const int MaxPlaylistPages = 200;

    public event EventHandler<TrackRatedEventArgs>? TrackRated;

    public event EventHandler<PlaylistChangedEventArgs>? PlaylistChanged;

    public async Task RateTrackAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default)
    {
        await api.RateSongAsync(videoId, status, cancellationToken).ConfigureAwait(false);
        TrackRated?.Invoke(this, new TrackRatedEventArgs(videoId, status));
    }

    public async Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default)
    {
        var playlistId = await api.CreatePlaylistAsync(title, description, privacy, videoIds, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Created, videoIds ?? []));
        return playlistId;
    }

    public async Task AddToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        await api.AddPlaylistItemsAsync(playlistId, videoIds, allowDuplicates, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.ItemsAdded, videoIds));
    }

    public async Task<IReadOnlyList<string>> AddMissingToPlaylistAsync(string playlistId, IReadOnlyList<string> videoIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(videoIds);

        var present = await GetPlaylistVideoIdsAsync(playlistId, cancellationToken).ConfigureAwait(false);
        List<string> missing = [.. videoIds.Distinct(StringComparer.Ordinal).Where(id => !present.Contains(id))];
        if (missing.Count == 0)
        {
            return [];
        }

        // Still with the server's duplicate check: the user asked for no duplicates, and the playlist may have changed since
        // it was read.
        await AddToPlaylistAsync(playlistId, missing, allowDuplicates: false, cancellationToken).ConfigureAwait(false);
        return missing;
    }

    public async Task RemoveFromPlaylistAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        await api.RemovePlaylistItemsAsync(playlistId, tracks, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.ItemsRemoved, [.. tracks.Select(t => t.VideoId)]));
    }

    public async Task MovePlaylistItemAsync(string playlistId, Track track, Track? successor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(track);
        var setVideoId = track.SetVideoId ?? throw new ArgumentException("The song has no playlist entry id. Do you own this playlist?", nameof(track));
        string? successorId = null;
        if (successor is not null)
        {
            successorId = successor.SetVideoId ?? throw new ArgumentException("The next song has no playlist entry id.", nameof(successor));
        }

        await api.MovePlaylistItemAsync(playlistId, setVideoId, successorId, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.ItemsMoved, [track.VideoId]));
    }

    public async Task<IReadOnlyList<Track>> RestorePlaylistItemsAsync(string playlistId, IReadOnlyList<RemovedPlaylistEntry> entries, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        if (entries.Count == 0)
        {
            return [];
        }

        // The entries are added back as new ones (with new set ids) at the end, in their old order. Duplicates are allowed:
        // the same song may still be in the playlist, and the user had it twice before.
        var added = await api.AddPlaylistItemsAsync(playlistId, [.. entries.Select(e => e.Track.VideoId)], allowDuplicates: true, cancellationToken)
            .ConfigureAwait(false);
        var newIds = MatchAddedEntries(entries, added);

        var removedIds = new HashSet<string>(StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < entries.Count; i++)
        {
            if (entries[i].Track.SetVideoId is { } oldId)
            {
                removedIds.Add(oldId);
                if (newIds[i] is { } newId)
                {
                    renamed[oldId] = newId;
                }
            }
        }

        // Back to front, so each entry's old successor is already in its final place when the entry moves in before it.
        // The last entry, and an entry that was followed by the next restored one, are already in place at the end.
        var stayedAtEnd = new bool[entries.Count];
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            if (newIds[i] is not { } newId)
            {
                continue;
            }

            var successor = entries[i].SuccessorSetVideoId;
            if (successor is null)
            {
                stayedAtEnd[i] = i == entries.Count - 1;
                continue;
            }

            if (i + 1 < entries.Count && stayedAtEnd[i + 1] && successor == entries[i + 1].Track.SetVideoId)
            {
                stayedAtEnd[i] = true;
                continue;
            }

            if (renamed.TryGetValue(successor, out var renamedSuccessor))
            {
                successor = renamedSuccessor;
            }
            else if (removedIds.Contains(successor))
            {
                continue; // its successor didn't come back: there is nothing to put it before
            }

            await api.MovePlaylistItemAsync(playlistId, newId, successor, cancellationToken).ConfigureAwait(false);
        }

        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.ItemsAdded, [.. entries.Select(e => e.Track.VideoId)]));
        return [.. entries.Select((e, i) => e.Track with { SetVideoId = newIds[i] })];
    }

    public async Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default)
    {
        await api.EditPlaylistAsync(playlistId, title, description, privacy, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Edited, []));
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        await api.DeletePlaylistAsync(playlistId, cancellationToken).ConfigureAwait(false);
        PlaylistChanged?.Invoke(this, new PlaylistChangedEventArgs(playlistId, PlaylistChangeKind.Deleted, []));
    }

    private async Task<HashSet<string>> GetPlaylistVideoIdsAsync(string playlistId, CancellationToken cancellationToken)
    {
        var page = await browse.GetPlaylistAsync(playlistId, cancellationToken).ConfigureAwait(false);
        var videoIds = new HashSet<string>(StringComparer.Ordinal);
        var tracks = page.Tracks;
        for (var pages = 1; ; pages++)
        {
            videoIds.UnionWith(tracks.Items.Select(t => t.VideoId));
            if (tracks.Continuation is not { } continuation || pages >= MaxPlaylistPages)
            {
                return videoIds;
            }

            tracks = await browse.GetPlaylistTracksAsync(continuation, cancellationToken).ConfigureAwait(false);
        }
    }

    // Pairs each entry with the first unused added entry of the same video, so a song listed twice gets two different ids.
    private static string?[] MatchAddedEntries(IReadOnlyList<RemovedPlaylistEntry> entries, IReadOnlyList<PlaylistEntryRef> added)
    {
        var newIds = new string?[entries.Count];
        var used = new bool[added.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            for (var j = 0; j < added.Count; j++)
            {
                if (!used[j] && added[j].VideoId == entries[i].Track.VideoId)
                {
                    used[j] = true;
                    newIds[i] = added[j].SetVideoId;
                    break;
                }
            }
        }

        return newIds;
    }
}
