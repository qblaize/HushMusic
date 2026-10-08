using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>Library pages (ytmusicapi <c>mixins/library.py</c>). All are auth-only.</summary>
internal sealed class LibraryApi(IInnerTubeClient client, ILogger<LibraryApi> logger) : ILibraryApi
{
    public Task<Paged<Playlist>> GetPlaylistsAsync(string? continuation = null, CancellationToken cancellationToken = default) =>
        GetLibraryPageAsync(
            "FEmusic_liked_playlists",
            ContinuationScope.LibraryPlaylists,
            continuation,
            response => LibraryParser.ParsePlaylists(response, logger),
            response => LibraryParser.ParsePlaylistsContinuation(response, logger),
            cancellationToken);

    public Task<Paged<Track>> GetSongsAsync(string? continuation = null, CancellationToken cancellationToken = default) =>
        GetLibraryPageAsync(
            "FEmusic_liked_videos",
            ContinuationScope.LibrarySongs,
            continuation,
            response => LibraryParser.ParseSongs(response, logger),
            response => LibraryParser.ParseSongsContinuation(response, logger),
            cancellationToken);

    public Task<Paged<Album>> GetAlbumsAsync(string? continuation = null, CancellationToken cancellationToken = default) =>
        GetLibraryPageAsync(
            "FEmusic_liked_albums",
            ContinuationScope.LibraryAlbums,
            continuation,
            response => LibraryParser.ParseAlbums(response, logger),
            response => LibraryParser.ParseAlbumsContinuation(response, logger),
            cancellationToken);

    public Task<Paged<Artist>> GetArtistsAsync(string? continuation = null, CancellationToken cancellationToken = default) =>
        GetLibraryPageAsync(
            "FEmusic_library_corpus_track_artists",
            ContinuationScope.LibraryArtists,
            continuation,
            response => LibraryParser.ParseArtists(response, logger),
            response => LibraryParser.ParseArtistsContinuation(response, logger),
            cancellationToken);

    /// <summary>ytmusicapi <c>get_liked_songs</c> = <c>get_playlist("LM")</c>, with the same continuation path.</summary>
    public async Task<Paged<Track>> GetLikedSongsAsync(string? continuation = null, CancellationToken cancellationToken = default)
    {
        if (continuation is not null)
        {
            return await PlaylistPaging.GetTracksAsync(client, continuation, requiresAuth: true, logger, cancellationToken).ConfigureAwait(false);
        }

        var page = await PlaylistPaging.GetPageAsync(client, "LM", requiresAuth: true, logger, cancellationToken).ConfigureAwait(false);
        return page.Tracks;
    }

    public async Task<IReadOnlyList<Shelf>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["browseId"] = "FEmusic_history" }) { RequiresAuth = true },
            cancellationToken).ConfigureAwait(false);
        return LibraryParser.ParseHistory(response, logger);
    }

    private Task<Paged<T>> GetLibraryPageAsync<T>(
        string browseId,
        string scope,
        string? continuation,
        Func<JsonNode, Paged<T>> parseFirstPage,
        Func<JsonNode, Paged<T>> parseContinuation,
        CancellationToken cancellationToken) =>
        QueryPaging.GetAsync(
            client,
            "browse",
            scope,
            new JsonObject { ["browseId"] = browseId },
            continuation,
            requiresAuth: true,
            parseFirstPage,
            parseContinuation,
            cancellationToken);
}
