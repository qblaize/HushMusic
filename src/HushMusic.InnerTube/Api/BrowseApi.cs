using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

internal sealed class BrowseApi(IInnerTubeClient client, ILogger<BrowseApi> logger) : IBrowseApi
{
    public Task<Paged<Shelf>> GetHomeAsync(string? continuation = null, CancellationToken cancellationToken = default) =>
        QueryPaging.GetAsync(
            client,
            "browse",
            ContinuationScope.Home,
            new JsonObject { ["browseId"] = "FEmusic_home" },
            continuation,
            requiresAuth: false,
            response => HomeParser.Parse(response, logger),
            response => HomeParser.ParseContinuation(response, logger),
            cancellationToken);

    public async Task<AlbumPage> GetAlbumAsync(string browseId, CancellationToken cancellationToken = default)
    {
        // Same check as ytmusicapi get_album: album pages need the MPREb_ browse id, not the OLAK5uy_ playlist id.
        if (string.IsNullOrEmpty(browseId) || !browseId.StartsWith("MPRE", StringComparison.Ordinal))
        {
            throw new ArgumentException("Invalid album browseId provided, must start with MPRE.", nameof(browseId));
        }

        var response = await client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["browseId"] = browseId }),
            cancellationToken).ConfigureAwait(false);
        return AlbumParser.Parse(response, browseId, logger);
    }

    public async Task<ArtistPage> GetArtistAsync(string channelId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        // ytmusicapi get_artist strips the "MPLA" prefix that some artist links carry.
        if (channelId.StartsWith("MPLA", StringComparison.Ordinal))
        {
            channelId = channelId[4..];
        }

        var response = await client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["browseId"] = channelId }),
            cancellationToken).ConfigureAwait(false);
        return ArtistParser.Parse(response, channelId, logger);
    }

    public Task<PlaylistPage> GetPlaylistAsync(string playlistId, CancellationToken cancellationToken = default) =>
        PlaylistPaging.GetPageAsync(client, playlistId, requiresAuth: false, logger, cancellationToken);

    public Task<Paged<Track>> GetPlaylistTracksAsync(string continuation, CancellationToken cancellationToken = default) =>
        PlaylistPaging.GetTracksAsync(client, continuation, requiresAuth: false, logger, cancellationToken);
}
