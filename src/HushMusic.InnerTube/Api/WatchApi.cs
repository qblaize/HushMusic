using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>
/// Watch queue, radio, lyrics, related and loudness (ytmusicapi <c>mixins/watch.py</c>, <c>get_lyrics</c>,
/// <c>get_song_related</c>, <c>get_song</c>).
/// </summary>
internal sealed partial class WatchApi(IInnerTubeClient client, TimeProvider timeProvider, ILogger<WatchApi> logger) : IWatchApi
{
    private const int LoudnessCacheCapacity = 512;

    private readonly Lock _loudnessGate = new();
    private readonly Dictionary<string, double?> _loudness = new(StringComparer.Ordinal);
    private readonly Queue<string> _loudnessOrder = new();

    public async Task<WatchPlaylist> GetWatchPlaylistAsync(string? videoId, string? playlistId = null, bool radio = false, bool shuffle = false, CancellationToken cancellationToken = default)
    {
        var body = BuildNextBody(videoId, playlistId, radio, shuffle);
        var response = await client.PostAsync(new InnerTubeRequest("next", body), cancellationToken).ConfigureAwait(false);

        if (JsonLookup.Get(
                response,
                "contents",
                "singleColumnMusicWatchNextResultsRenderer",
                "tabbedRenderer",
                "watchNextTabbedResultsRenderer",
                "tabs",
                0,
                "tabRenderer",
                "content",
                "musicQueueRenderer",
                "content",
                "playlistPanelRenderer") is null)
        {
            var message = "No content returned by the server.";
            if (JsonLookup.GetString(body, "playlistId") is { } requested)
            {
                message += $" Ensure you have access to {requested} - a private playlist may cause this.";
            }

            throw new InnerTubeException("next", message);
        }

        var result = WatchParser.Parse(response, logger);
        return result with { Continuation = ContinuationToken.Wrap(ContinuationScope.Watch, result.Continuation, body) };
    }

    public async Task<Paged<Track>> GetWatchPlaylistContinuationAsync(string continuation, CancellationToken cancellationToken = default)
    {
        var state = ContinuationToken.Unwrap(continuation, ContinuationScope.Watch);
        var body = state.RequireBody();
        var response = await client.PostAsync(
            new InnerTubeRequest("next", body) { QueryContinuation = state.Token },
            cancellationToken).ConfigureAwait(false);

        var page = WatchParser.ParseContinuation(response, logger);
        return page with
        {
            Continuation = page.Items.Count == 0 ? null : ContinuationToken.Wrap(ContinuationScope.Watch, page.Continuation, body),
        };
    }

    public async Task<Lyrics?> GetLyricsAsync(string lyricsBrowseId, bool timestamps = false, CancellationToken cancellationToken = default)
    {
        // ytmusicapi raises "This song might not have lyrics" for an empty id; the contract returns null.
        if (string.IsNullOrWhiteSpace(lyricsBrowseId))
        {
            return null;
        }

        var body = new JsonObject { ["browseId"] = lyricsBrowseId };
        if (timestamps && await GetMobileLyricsAsync(body, cancellationToken).ConfigureAwait(false) is { } mobile)
        {
            return mobile;
        }

        var response = await client.PostAsync(new InnerTubeRequest("browse", body), cancellationToken).ConfigureAwait(false);
        return LyricsParser.Parse(response, logger);
    }

    public async Task<IReadOnlyList<Shelf>> GetRelatedAsync(string relatedBrowseId, CancellationToken cancellationToken = default)
    {
        // ytmusicapi raises "Invalid browseId provided." for an empty id; there is nothing to show.
        if (string.IsNullOrWhiteSpace(relatedBrowseId))
        {
            return [];
        }

        var response = await client.PostAsync(
            new InnerTubeRequest("browse", new JsonObject { ["browseId"] = relatedBrowseId }),
            cancellationToken).ConfigureAwait(false);
        return RelatedParser.Parse(response, logger);
    }

    public async Task<double?> GetLoudnessDbAsync(string videoId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        lock (_loudnessGate)
        {
            if (_loudness.TryGetValue(videoId, out var cached))
            {
                return cached;
            }
        }

        // Sent without the account: loudness is the same for everyone (verified anonymously on 2026-10-07), and an
        // extra signed-in "player" call for every played track is traffic the account does not need.
        var response = await client.PostAsync(
            new InnerTubeRequest(PlayerRequest.Endpoint, PlayerRequest.Body(videoId, timeProvider.GetUtcNow())) { Anonymous = true },
            cancellationToken).ConfigureAwait(false);
        var loudness = PlayerParser.ParseLoudnessDb(response, logger);

        lock (_loudnessGate)
        {
            if (_loudness.TryAdd(videoId, loudness))
            {
                _loudnessOrder.Enqueue(videoId);
                if (_loudnessOrder.Count > LoudnessCacheCapacity)
                {
                    _loudness.Remove(_loudnessOrder.Dequeue());
                }
            }
        }

        return loudness;
    }

    /// <summary>
    /// ytmusicapi <c>get_lyrics(timestamps=True)</c>: the same browse, sent as the ANDROID_MUSIC client, which answers
    /// with a <c>timedLyricsModel</c> (lines without cue ranges when the lyrics are not synced). Returns null when that
    /// response has no lyrics or the server refuses the mobile client, so the caller can ask the web client
    /// (ytmusicapi would return None there).
    /// </summary>
    private async Task<Lyrics?> GetMobileLyricsAsync(JsonObject body, CancellationToken cancellationToken)
    {
        try
        {
            // Sent without the account, like ytmusicapi's own (unauthenticated) test of this call: lyrics are the same for
            // everyone, and a signed-in session must never be judged by its answer to an Android client.
            var response = await client.PostAsync(
                new InnerTubeRequest("browse", body) { Client = ClientProfile.Mobile, Anonymous = true },
                cancellationToken).ConfigureAwait(false);
            return LyricsParser.Parse(response, logger);
        }
        catch (Exception ex) when (ex is AuthRequiredException or InnerTubeException { StatusCode: not null })
        {
            LogMobileLyricsFailed(logger, ex.Message);
            return null;
        }
    }

    /// <summary>The <c>next</c> body exactly as ytmusicapi <c>get_watch_playlist</c> builds it.</summary>
    internal static JsonObject BuildNextBody(string? videoId, string? playlistId, bool radio, bool shuffle)
    {
        if (string.IsNullOrEmpty(videoId) && string.IsNullOrEmpty(playlistId))
        {
            throw new ArgumentException("You must provide either a video id, a playlist id, or both.");
        }

        var body = new JsonObject
        {
            ["enablePersistentPlaylistPanel"] = true,
            ["isAudioOnly"] = true,
            ["tunerSettingValue"] = "AUTOMIX_SETTING_NORMAL",
        };

        if (!string.IsNullOrEmpty(videoId))
        {
            body["videoId"] = videoId;

            // A lone video plays as its song radio, like pressing play on a single song in the web app.
            if (string.IsNullOrEmpty(playlistId))
            {
                playlistId = "RDAMVM" + videoId;
            }

            if (!(radio || shuffle))
            {
                body["watchEndpointMusicSupportedConfigs"] = new JsonObject
                {
                    ["watchEndpointMusicConfig"] = new JsonObject
                    {
                        ["hasPersistentPlaylistPanel"] = true,
                        ["musicVideoType"] = "MUSIC_VIDEO_TYPE_ATV",
                    },
                };
            }
        }

        if (!string.IsNullOrEmpty(playlistId))
        {
            body["playlistId"] = PlaylistPaging.StripBrowsePrefix(playlistId);
        }

        // Radio wins over shuffle. The "%3D" is literal, as in ytmusicapi.
        if (shuffle && playlistId is not null)
        {
            body["params"] = "wAEB8gECKAE%3D";
        }

        if (radio)
        {
            body["params"] = "wAEB";
        }

        return body;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Timed lyrics request (mobile client) failed, falling back to plain lyrics: {Reason}")]
    private static partial void LogMobileLyricsFailed(ILogger logger, string reason);
}
