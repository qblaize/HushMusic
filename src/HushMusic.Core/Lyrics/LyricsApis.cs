using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Lyrics;

/// <summary>
/// A YouTube video's own caption track read as lyrics: the caption tracks of the <c>player</c> response, fetched as
/// json3. Implemented in HushMusic.InnerTube. This goes beyond ytmusicapi; docs/innertube-requests.md has the details.
/// </summary>
public interface ICaptionLyricsApi
{
    /// <summary>
    /// The lines of the video's caption track in the song's language (never an auto-generated one), or null when the
    /// video has no such track. Network errors propagate.
    /// </summary>
    Task<CaptionLyrics?> GetCaptionLyricsAsync(string videoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// A caption track as the video has it: times as given, text as written (music notes and sound descriptions included;
/// <see cref="LyricsCleanup"/> tidies it up).
/// </summary>
public sealed record CaptionLyrics(string LanguageCode, IReadOnlyList<LyricLine> Lines)
{
    /// <summary>
    /// The track's language matches the video's auto-generated track, which follows the spoken (sung) language. False
    /// when the track was picked only because it is the video's one language.
    /// </summary>
    public bool LanguageConfirmed { get; init; }

    /// <summary>The track's name as YouTube shows it, e.g. "English".</summary>
    public string? TrackName { get; init; }
}

/// <summary>The YouTube Music lyrics browse id ("MPLY…") of a track, from the Lyrics tab of its watch ("next") response.</summary>
public interface ILyricsBrowseIdSource
{
    /// <summary>Null when the track has no lyrics tab.</summary>
    Task<string?> GetLyricsBrowseIdAsync(string videoId, CancellationToken cancellationToken = default);
}

/// <summary>
/// <see cref="ILyricsBrowseIdSource"/> on a watch-playlist lookup: by default <see cref="IWatchApi.GetWatchPlaylistAsync"/>;
/// the app passes its shared lookup so that the lyrics and "Related" tabs make one "next" call per track.
/// </summary>
public sealed class WatchLyricsBrowseIds(Func<string, CancellationToken, Task<WatchPlaylist>> getWatchPlaylist) : ILyricsBrowseIdSource
{
    public WatchLyricsBrowseIds(IWatchApi watch)
        : this((videoId, cancellationToken) => watch.GetWatchPlaylistAsync(videoId, cancellationToken: cancellationToken))
    {
    }

    public async Task<string?> GetLyricsBrowseIdAsync(string videoId, CancellationToken cancellationToken = default)
    {
        var watch = await getWatchPlaylist(videoId, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(watch.LyricsBrowseId) ? null : watch.LyricsBrowseId;
    }
}
