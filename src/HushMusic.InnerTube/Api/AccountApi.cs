using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>Account info and write actions (ytmusicapi <c>mixins/library.py</c>, <c>mixins/playlists.py</c>). All auth-only.</summary>
internal sealed partial class AccountApi(IInnerTubeClient client, TimeProvider timeProvider, ILogger<AccountApi> logger) : IAccountApi
{
    private const string EditPlaylistEndpoint = "browse/edit_playlist";
    private const string StatusSucceeded = "STATUS_SUCCEEDED";

    // Client playback nonce alphabet from ytmusicapi add_history_item.
    private const string CpnAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_";

    public async Task<AccountInfo?> GetAccountInfoAsync(CancellationToken cancellationToken = default)
    {
        var response = await client.PostAsync(
            new InnerTubeRequest("account/account_menu", new JsonObject())
            {
                RequiresAuth = true,
                SignedOutCheck = SignedOutAccountMenu,
            },
            cancellationToken).ConfigureAwait(false);
        return AccountParser.ParseAccountInfo(response, logger);
    }

    public async Task RateSongAsync(string videoId, LikeStatus status, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        var endpoint = status switch
        {
            LikeStatus.Like => "like/like",
            LikeStatus.Dislike => "like/dislike",
            LikeStatus.Indifferent => "like/removelike",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown rating."),
        };

        var body = new JsonObject { ["target"] = new JsonObject { ["videoId"] = videoId } };
        var response = await client.PostAsync(new InnerTubeRequest(endpoint, body) { RequiresAuth = true }, cancellationToken).ConfigureAwait(false);
        EnsurePerformed(response, endpoint);
    }

    public async Task<string> CreatePlaylistAsync(string title, string? description, PrivacyStatus privacy, IReadOnlyList<string>? videoIds = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        // ytmusicapi rejects these because YouTube Music breaks on them.
        if (title.Contains('<') || title.Contains('>'))
        {
            throw new ArgumentException("Playlist titles cannot contain < or >.", nameof(title));
        }

        var body = new JsonObject
        {
            ["title"] = title,
            ["description"] = StripHtmlTags(description ?? string.Empty),
            ["privacyStatus"] = ToPrivacyValue(privacy),
        };

        if (videoIds is not null)
        {
            body["videoIds"] = new JsonArray(videoIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        }

        const string endpoint = "playlist/create";
        var response = await client.PostAsync(
            new InnerTubeRequest(endpoint, body) { RequiresAuth = true, AllowRetry = false },
            cancellationToken).ConfigureAwait(false);

        if (JsonLookup.GetString(response, "playlistId") is { Length: > 0 } playlistId)
        {
            return playlistId;
        }

        EnsurePerformed(response, endpoint);
        throw new InnerTubeException(endpoint, "YouTube Music did not return an id for the new playlist.");
    }

    public async Task<IReadOnlyList<PlaylistEntryRef>> AddPlaylistItemsAsync(string playlistId, IReadOnlyList<string> videoIds, bool allowDuplicates = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentNullException.ThrowIfNull(videoIds);
        if (videoIds.Count == 0)
        {
            throw new ArgumentException("Provide at least one video id to add to the playlist.", nameof(videoIds));
        }

        var actions = new JsonArray();
        foreach (var videoId in videoIds)
        {
            var action = new JsonObject { ["action"] = "ACTION_ADD_VIDEO", ["addedVideoId"] = videoId };

            // Counter-intuitive but per ytmusicapi: DEDUPE_OPTION_SKIP skips the server's duplicate check,
            // so duplicates ARE added. Without it a duplicate makes the whole request fail.
            if (allowDuplicates)
            {
                action["dedupeOption"] = "DEDUPE_OPTION_SKIP";
            }

            actions.Add(action);
        }

        var response = await PostEditPlaylistAsync(playlistId, actions, allowRetry: false, cancellationToken).ConfigureAwait(false);
        EnsurePerformed(response, EditPlaylistEndpoint);

        // ytmusicapi add_playlist_items(duplicates=False): if a song is already in the playlist, the request fails and nothing
        // is added. ytmusicapi only documents the failed status (it hands back the raw response), so any status other than
        // STATUS_SUCCEEDED is taken as that refusal.
        if (!allowDuplicates && JsonLookup.GetString(response, "status") is { } status && status != StatusSucceeded)
        {
            LogAddRefused(logger, videoIds.Count, status);
            throw new AlreadyInPlaylistException(
                playlistId,
                videoIds,
                new InnerTubeException(EditPlaylistEndpoint, $"YouTube Music did not apply the change (status {status})."));
        }

        EnsureSucceeded(response, EditPlaylistEndpoint);
        return ParseAddedEntries(response);
    }

    public async Task RemovePlaylistItemsAsync(string playlistId, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentNullException.ThrowIfNull(tracks);

        var actions = new JsonArray();
        foreach (var track in tracks)
        {
            if (string.IsNullOrEmpty(track.SetVideoId) || string.IsNullOrEmpty(track.VideoId))
            {
                continue;
            }

            actions.Add(new JsonObject
            {
                ["setVideoId"] = track.SetVideoId,
                ["removedVideoId"] = track.VideoId,
                ["action"] = "ACTION_REMOVE_VIDEO",
            });
        }

        if (actions.Count == 0)
        {
            throw new ArgumentException("Cannot remove songs, because setVideoId is missing. Do you own this playlist?", nameof(tracks));
        }

        await EditPlaylistCoreAsync(playlistId, actions, allowRetry: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task MovePlaylistItemAsync(string playlistId, string setVideoId, string? successorSetVideoId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);
        ArgumentException.ThrowIfNullOrWhiteSpace(setVideoId);

        // ytmusicapi edit_playlist(moveItem=setVideoId or (setVideoId, successor)): the successor key only when there is one.
        var action = new JsonObject { ["action"] = "ACTION_MOVE_VIDEO_BEFORE", ["setVideoId"] = setVideoId };
        if (!string.IsNullOrEmpty(successorSetVideoId))
        {
            action["movedSetVideoIdSuccessor"] = successorSetVideoId;
        }

        await EditPlaylistCoreAsync(playlistId, [action], allowRetry: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task EditPlaylistAsync(string playlistId, string? title = null, string? description = null, PrivacyStatus? privacy = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        // Same truthiness rules as ytmusicapi edit_playlist: empty strings are ignored.
        var actions = new JsonArray();
        if (!string.IsNullOrEmpty(title))
        {
            actions.Add(new JsonObject { ["action"] = "ACTION_SET_PLAYLIST_NAME", ["playlistName"] = title });
        }

        if (!string.IsNullOrEmpty(description))
        {
            actions.Add(new JsonObject { ["action"] = "ACTION_SET_PLAYLIST_DESCRIPTION", ["playlistDescription"] = description });
        }

        if (privacy is { } newPrivacy)
        {
            actions.Add(new JsonObject { ["action"] = "ACTION_SET_PLAYLIST_PRIVACY", ["playlistPrivacy"] = ToPrivacyValue(newPrivacy) });
        }

        if (actions.Count == 0)
        {
            return;
        }

        await EditPlaylistCoreAsync(playlistId, actions, allowRetry: true, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePlaylistAsync(string playlistId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(playlistId);

        const string endpoint = "playlist/delete";
        var body = new JsonObject { ["playlistId"] = PlaylistPaging.StripBrowsePrefix(playlistId) };
        var response = await client.PostAsync(new InnerTubeRequest(endpoint, body) { RequiresAuth = true }, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(response, endpoint);
    }

    public async Task AddHistoryItemAsync(string videoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoId);

        // ytmusicapi get_song: the tracking URL is only tied to the account when "player" is called
        // with the same signed-in session as the ping below (issue #703).
        var body = PlayerRequest.Body(videoId, timeProvider.GetUtcNow());
        var player = await client.PostAsync(new InnerTubeRequest(PlayerRequest.Endpoint, body) { RequiresAuth = true }, cancellationToken).ConfigureAwait(false);

        var baseUrl = JsonLookup.GetString(player, "playbackTracking", "videostatsPlaybackUrl", "baseUrl");
        if (string.IsNullOrEmpty(baseUrl) || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var trackingUri))
        {
            throw new InnerTubeException("player", "YouTube Music returned no playback tracking URL for this track.");
        }

        var separator = string.IsNullOrEmpty(trackingUri.Query) ? "?" : "&";
        var cpn = RandomNumberGenerator.GetString(CpnAlphabet, 16);
        var ping = new Uri(baseUrl + separator + "ver=2&c=WEB_REMIX&cpn=" + cpn);

        // Success is HTTP 204, which YouTube also returns when nothing was recorded (#703).
        await client.SendTrackingPingAsync(ping, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>ytmusicapi <c>html_to_txt</c>: removes every <c>&lt;…&gt;</c> tag.</summary>
    internal static string StripHtmlTags(string text) => HtmlTagRegex().Replace(text, string.Empty);

    private static string ToPrivacyValue(PrivacyStatus privacy) => privacy switch
    {
        PrivacyStatus.Public => "PUBLIC",
        PrivacyStatus.Unlisted => "UNLISTED",
        PrivacyStatus.Private => "PRIVATE",
        _ => throw new ArgumentOutOfRangeException(nameof(privacy), privacy, "Unknown privacy status."),
    };

    /// <summary>
    /// ytmusicapi <c>add_playlist_items</c>: <c>playlistEditResults[].playlistEditVideoAddedResultData</c> holds the video id
    /// and the new set id of each added entry.
    /// </summary>
    internal static IReadOnlyList<PlaylistEntryRef> ParseAddedEntries(JsonNode response)
    {
        if (JsonLookup.Get(response, "playlistEditResults") is not JsonArray results)
        {
            return [];
        }

        List<PlaylistEntryRef> entries = [];
        foreach (var result in results)
        {
            var data = JsonLookup.Get(result, "playlistEditVideoAddedResultData");
            if (JsonLookup.GetString(data, "videoId") is { Length: > 0 } videoId && JsonLookup.GetString(data, "setVideoId") is { Length: > 0 } setVideoId)
            {
                entries.Add(new PlaylistEntryRef(videoId, setVideoId));
            }
        }

        return entries;
    }

    private async Task EditPlaylistCoreAsync(string playlistId, JsonArray actions, bool allowRetry, CancellationToken cancellationToken)
    {
        var response = await PostEditPlaylistAsync(playlistId, actions, allowRetry, cancellationToken).ConfigureAwait(false);
        EnsureSucceeded(response, EditPlaylistEndpoint);
    }

    private Task<JsonNode> PostEditPlaylistAsync(string playlistId, JsonArray actions, bool allowRetry, CancellationToken cancellationToken)
    {
        var body = new JsonObject
        {
            ["playlistId"] = PlaylistPaging.StripBrowsePrefix(playlistId),
            ["actions"] = actions,
        };

        return client.PostAsync(
            new InnerTubeRequest(EditPlaylistEndpoint, body) { RequiresAuth = true, AllowRetry = allowRetry },
            cancellationToken);
    }

    /// <summary>
    /// ytmusicapi <c>validate_write_response</c>: HTTP 200 with only a <c>showEngagementPanelEndpoint</c>
    /// action means YouTube Music did not perform the write and wants the user to go through a dialog.
    /// </summary>
    private static void EnsurePerformed(JsonNode response, string endpoint)
    {
        if (JsonLookup.Get(response, "actions", 0, "showEngagementPanelEndpoint") is { } panel)
        {
            var tag = JsonLookup.GetString(panel, "identifier", "tag") ?? "unknown";
            throw new InnerTubeException(
                endpoint,
                $"YouTube Music did not perform this request and asked for interaction with its '{tag}' dialog. The account may be temporarily restricted from this action.");
        }
    }

    private void EnsureSucceeded(JsonNode response, string endpoint)
    {
        EnsurePerformed(response, endpoint);

        var status = JsonLookup.GetString(response, "status");
        if (status is null)
        {
            LogMissingStatus(logger, endpoint);
            return;
        }

        if (status != StatusSucceeded)
        {
            throw new InnerTubeException(endpoint, $"YouTube Music did not apply the change (status {status}).");
        }
    }

    // A signed-out account menu has the same popup but no active-account header.
    private static string? SignedOutAccountMenu(JsonNode response)
    {
        var menu = JsonLookup.Get(response, "actions", 0, "openPopupAction", "popup", "multiPageMenuRenderer");
        return menu is not null && JsonLookup.Get(menu, "header", "activeAccountHeaderRenderer") is null
            ? "account menu has no active account"
            : null;
    }

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [LoggerMessage(Level = LogLevel.Warning, Message = "InnerTube {Endpoint} response has no status field; assuming success")]
    private static partial void LogMissingStatus(ILogger logger, string endpoint);

    [LoggerMessage(Level = LogLevel.Information, Message = "Adding {Count} songs to a playlist was refused with status {Status}; treating it as songs already in the playlist")]
    private static partial void LogAddRefused(ILogger logger, int count, string status);
}
