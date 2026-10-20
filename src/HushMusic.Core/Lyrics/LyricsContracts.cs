using HushMusic.Core.Models;

namespace HushMusic.Core.Lyrics;

/// <summary>How precisely a lyrics document is timed.</summary>
public enum LyricsSyncLevel
{
    /// <summary>Plain text, no timing.</summary>
    Unsynced,

    /// <summary>Each line has a start time.</summary>
    Line,

    /// <summary>Each word has a start and end time.</summary>
    Word,

    /// <summary>Each syllable has a start and end time.</summary>
    Syllable,
}

/// <summary>One timed word (or syllable) of a line. <see cref="Text"/> includes its trailing space, if any.</summary>
public sealed record LyricWord(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// One lyrics line. <see cref="Words"/> is empty when only the line is timed. When the source gives no end time,
/// <see cref="End"/> is the next line's start (or the end of the song).
/// </summary>
public sealed record LyricLine(TimeSpan Start, TimeSpan End, string Text, IReadOnlyList<LyricWord> Words)
{
    /// <summary>Backing vocals or ad-libs sung under the main line.</summary>
    public bool IsBackground { get; init; }
}

/// <summary>
/// Lyrics for one track from one source. Unsynced documents have one <see cref="LyricLine"/> per text line, with
/// <see cref="LyricLine.Start"/> and <see cref="LyricLine.End"/> set to <see cref="TimeSpan.Zero"/>; an empty line
/// there separates two verses (never two in a row, never first or last). Synced documents have no empty lines: an
/// instrumental break is the gap between one line's <see cref="LyricLine.End"/> and the next line's start.
/// </summary>
public sealed record LyricsDocument(string SourceKey, string SourceName, LyricsSyncLevel Sync, IReadOnlyList<LyricLine> Lines)
{
    /// <summary>A page about the lyrics at the source, when it has one.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>The source says the track has no vocals; <see cref="LyricsDocument.Lines"/> is then empty.</summary>
    public bool IsInstrumental { get; init; }

    /// <summary>Songwriter credits, when the source names them.</summary>
    public string? Writers { get; init; }

    /// <summary>The source's own credit line, e.g. "Source: LyricFind" for YouTube Music's lyrics.</summary>
    public string? Attribution { get; init; }
}

/// <summary>One entry of the lyrics source list, e.g. ("lrclib-synced", "LRCLIB", Line).</summary>
public sealed record LyricsSourceInfo(string Key, string ProviderName, LyricsSyncLevel Sync);

/// <summary>A source entry as the user arranged it.</summary>
public sealed record LyricsSourceSetting(string Key, bool Enabled);

/// <summary>
/// Lyrics from several sources (YouTube Music, a video's captions, LRCLIB) in the order the user chose. Results are
/// cached, so asking again for the same track (or switching between its sources) doesn't refetch. Thread-safe.
/// </summary>
public interface ILyricsService
{
    /// <summary>Every source entry, in the default order (best first).</summary>
    IReadOnlyList<LyricsSourceInfo> Sources { get; }

    /// <summary>
    /// The user's order: every entry of <see cref="Sources"/> exactly once. Saved entries keep their place and
    /// switch; entries added since are slotted in after their default predecessor.
    /// </summary>
    IReadOnlyList<LyricsSourceSetting> GetSourceOrder();

    /// <summary>Saves the order (unknown keys are dropped, missing ones are added) and raises <see cref="SourceOrderChanged"/>.</summary>
    Task SetSourceOrderAsync(IReadOnlyList<LyricsSourceSetting> order, CancellationToken cancellationToken = default);

    /// <summary>
    /// The lyrics of the first enabled source, in the user's order, that has lyrics for the track at its level.
    /// Null when none has (and always for live radio). A source that can't be reached is skipped; when none of the
    /// sources asked could be reached, a <see cref="HushException"/> says so.
    /// </summary>
    Task<LyricsDocument?> GetLyricsAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>
    /// The lyrics of one source entry (enabled or not), or null when it has none for the track (or the key is
    /// unknown). Throws a <see cref="HushException"/> when the source can't be reached.
    /// </summary>
    Task<LyricsDocument?> GetLyricsAsync(Track track, string sourceKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// The enabled source entries known to have lyrics for the track, in the user's order. Cheap: it only looks at
    /// what was already fetched (memory or disk cache) and never makes a request.
    /// </summary>
    Task<IReadOnlyList<LyricsSourceInfo>> GetAvailableSourcesAsync(Track track, CancellationToken cancellationToken = default);

    /// <summary>The source order or a source's switch changed.</summary>
    event EventHandler? SourceOrderChanged;
}
