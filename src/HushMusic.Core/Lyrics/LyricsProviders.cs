using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Lyrics;

/// <summary>One lyrics source. A single fetch answers every entry of the source (synced and plain alike).</summary>
internal interface ILyricsProvider
{
    LyricsProviderId Id { get; }

    /// <summary>False when the source is never asked about this track.</summary>
    bool Supports(Track track);

    /// <summary>Every document the source has for the track (none when it has no lyrics). Network errors propagate.</summary>
    Task<IReadOnlyList<LyricsDocument>> FetchAsync(Track track, CancellationToken cancellationToken);
}

/// <summary>
/// YouTube Music's own lyrics: the lyrics browse id from the watch ("next") response, then <c>browse</c> as the mobile
/// client for timed lines (<see cref="IWatchApi.GetLyricsAsync"/>). One call fills both YouTube entries.
/// </summary>
internal sealed class YouTubeLyricsProvider(IWatchApi watch, ILyricsBrowseIdSource browseIds) : ILyricsProvider
{
    private const string ProviderName = "YouTube";

    public LyricsProviderId Id => LyricsProviderId.YouTube;

    public bool Supports(Track track) => !track.IsLiveRadio;

    public async Task<IReadOnlyList<LyricsDocument>> FetchAsync(Track track, CancellationToken cancellationToken)
    {
        var browseId = await browseIds.GetLyricsBrowseIdAsync(track.VideoId, cancellationToken).ConfigureAwait(false);
        if (browseId is null)
        {
            return [];
        }

        var lyrics = await watch.GetLyricsAsync(browseId, timestamps: true, cancellationToken).ConfigureAwait(false);
        if (lyrics is null)
        {
            return [];
        }

        var url = "https://music.youtube.com/watch?v=" + Uri.EscapeDataString(track.VideoId);
        var documents = new List<LyricsDocument>(2);
        if (lyrics.TimedLines is { Count: > 0 } timed)
        {
            var lines = LyricsCleanup.Timed(timed.Select(l => new LyricLine(l.Start, l.End, l.Text, [])), track.Duration);
            if (lines.Count > 0)
            {
                documents.Add(new LyricsDocument(LyricsSources.YouTubeSynced, ProviderName, LyricsSyncLevel.Line, lines)
                {
                    SourceUrl = url,
                    Attribution = lyrics.Source,
                });
            }
        }

        var plain = LyricsCleanup.Plain(lyrics.Text);
        if (plain.Count > 0)
        {
            documents.Add(new LyricsDocument(LyricsSources.YouTubePlain, ProviderName, LyricsSyncLevel.Unsynced, plain)
            {
                SourceUrl = url,
                Attribution = lyrics.Source,
                Writers = LyricsCleanup.FindWriters(lyrics.Text.ReplaceLineEndings("\n").Split('\n')),
            });
        }

        return documents;
    }
}

/// <summary>A video's own caption track (<see cref="ICaptionLyricsApi"/>), for music videos with lyric captions.</summary>
internal sealed class CaptionLyricsProvider(ICaptionLyricsApi captions) : ILyricsProvider
{
    private const string ProviderName = "YouTube captions";

    public LyricsProviderId Id => LyricsProviderId.YouTubeCaptions;

    // Episodes have spoken captions, not lyrics.
    public bool Supports(Track track) => !track.IsLiveRadio && track.Type != TrackType.Episode;

    public async Task<IReadOnlyList<LyricsDocument>> FetchAsync(Track track, CancellationToken cancellationToken)
    {
        var caption = await captions.GetCaptionLyricsAsync(track.VideoId, cancellationToken).ConfigureAwait(false);
        if (caption is null)
        {
            return [];
        }

        var lines = LyricsCleanup.Timed(caption.Lines, track.Duration, LyricsTextKind.Captions);
        if (lines.Count < LrcLib.MinimumLines || (!caption.LanguageConfirmed && LooksTranslated(track.Title, lines)))
        {
            return [];
        }

        return
        [
            new LyricsDocument(LyricsSources.YouTubeCaptions, ProviderName, LyricsSyncLevel.Line, lines)
            {
                SourceUrl = "https://www.youtube.com/watch?v=" + Uri.EscapeDataString(track.VideoId),
                Attribution = caption.TrackName,
            },
        ];
    }

    /// <summary>
    /// A title in a non-Latin script with captions in Latin letters is most likely a translation (the song's own
    /// language could not be confirmed, see <see cref="CaptionLyrics.LanguageConfirmed"/>).
    /// </summary>
    internal static bool LooksTranslated(string title, IReadOnlyList<LyricLine> lines) =>
        LatinShare(title) is < 0.5 and >= 0 && LatinShare(string.Concat(lines.Select(l => l.Text))) > 0.8;

    /// <summary>The share of letters that are Latin, or -1 when there are no letters.</summary>
    private static double LatinShare(string text)
    {
        int letters = 0, latin = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c))
            {
                continue;
            }

            letters++;
            if (c < 'ɐ')
            {
                latin++;
            }
        }

        return letters == 0 ? -1 : (double)latin / letters;
    }
}

/// <summary>LRCLIB (<see cref="LrcLibClient"/>): one lookup fills the word-synced, synced and plain entries.</summary>
internal sealed class LrcLibLyricsProvider(LrcLibClient client) : ILyricsProvider
{
    public LyricsProviderId Id => LyricsProviderId.LrcLib;

    public bool Supports(Track track) => !track.IsLiveRadio && track.Type != TrackType.Episode && track.Artists.Count > 0;

    public async Task<IReadOnlyList<LyricsDocument>> FetchAsync(Track track, CancellationToken cancellationToken)
    {
        if (LyricsQuery.From(track) is not { } query)
        {
            return [];
        }

        var record = await client.FindAsync(query, cancellationToken).ConfigureAwait(false);
        return record is null ? [] : LrcLib.ToDocuments(record, track.Duration);
    }
}
