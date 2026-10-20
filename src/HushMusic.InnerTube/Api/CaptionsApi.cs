using Microsoft.Extensions.Logging;
using HushMusic.Core;
using HushMusic.Core.Lyrics;
using HushMusic.InnerTube.Http;
using HushMusic.InnerTube.Parsing;

namespace HushMusic.InnerTube.Api;

/// <summary>
/// A video's caption track as lyrics. Beyond ytmusicapi (see docs/innertube-requests.md, "Captions as lyrics"): the
/// caption list comes from the same anonymous <c>player</c> request as loudness (reused when
/// <see cref="WatchApi"/> already made it), and the chosen track is one anonymous GET of its <c>baseUrl</c> with
/// <c>fmt=json3</c>. No account, no proof-of-origin token: tracks that would need one are skipped.
/// </summary>
internal sealed partial class CaptionsApi(
    IInnerTubeClient client,
    CaptionTrackCache trackCache,
    TimeProvider timeProvider,
    ILogger<CaptionsApi> logger) : ICaptionLyricsApi
{
    private const string TimedTextLabel = "api/timedtext";

    public async Task<CaptionLyrics?> GetCaptionLyricsAsync(string videoId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(videoId))
        {
            return null;
        }

        var cached = trackCache.TryGet(videoId);
        try
        {
            return await ReadAsync(videoId, cached ?? await FetchTracksAsync(videoId, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InnerTubeException ex) when (cached is not null && ex.StatusCode is not null)
        {
            // The signed track URL may have expired: ask the player for fresh ones once.
            LogRetry(logger, videoId, ex.Message);
            trackCache.Remove(videoId);
            return await ReadAsync(videoId, await FetchTracksAsync(videoId, cancellationToken).ConfigureAwait(false), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task<CaptionLyrics?> ReadAsync(string videoId, IReadOnlyList<CaptionTrack> tracks, CancellationToken cancellationToken)
    {
        if (CaptionsParser.PickLyricsTrack(tracks) is not { } picked || CaptionsParser.Json3Url(picked.Track.BaseUrl) is not { } url)
        {
            LogNoTrack(logger, videoId, tracks.Count);
            return null;
        }

        var (track, confirmed) = picked;
        var json = await client.GetPublicJsonAsync(url, TimedTextLabel, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            LogEmpty(logger, videoId, track.LanguageCode);
            return null;
        }

        var lines = CaptionsParser.ParseJson3(json, logger);
        return lines.Count == 0
            ? null
            : new CaptionLyrics(track.LanguageCode, lines) { LanguageConfirmed = confirmed, TrackName = track.Name };
    }

    private async Task<IReadOnlyList<CaptionTrack>> FetchTracksAsync(string videoId, CancellationToken cancellationToken)
    {
        // The same anonymous request as loudness (ytmusicapi get_song body): captions are the same for everyone.
        var response = await client.PostAsync(
            new InnerTubeRequest(PlayerRequest.Endpoint, PlayerRequest.Body(videoId, timeProvider.GetUtcNow())) { Anonymous = true },
            cancellationToken).ConfigureAwait(false);
        var tracks = CaptionsParser.ParseTracks(response);
        trackCache.Store(videoId, tracks);
        return tracks;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "No caption track to read as lyrics for {VideoId} ({Count} track(s))")]
    private static partial void LogNoTrack(ILogger logger, string videoId, int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Caption track {Language} of {VideoId} came back empty")]
    private static partial void LogEmpty(ILogger logger, string videoId, string language);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Caption request for {VideoId} failed ({Reason}); asking the player for fresh track URLs")]
    private static partial void LogRetry(ILogger logger, string videoId, string reason);
}
