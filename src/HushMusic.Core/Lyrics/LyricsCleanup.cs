using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HushMusic.Core.Lyrics;

/// <summary>Where lyric text comes from, which decides how much of it is noise.</summary>
public enum LyricsTextKind
{
    /// <summary>Lyrics written as lyrics (YouTube Music, LRC files).</summary>
    Lyrics,

    /// <summary>A video's captions: music notes around lines, "[Music]" and similar sound descriptions, dialogue dashes.</summary>
    Captions,
}

/// <summary>
/// Turns lines from any source into the shape <see cref="LyricsDocument"/> promises: synced lines in time order with
/// their end times, no empty or credit lines, repeated captions merged; plain text with single blank lines between
/// verses.
/// </summary>
public static partial class LyricsCleanup
{
    /// <summary>How long the last synced line lasts when the song's length is unknown.</summary>
    public static readonly TimeSpan LastLineFallback = TimeSpan.FromSeconds(5);

    // Lines that start this close together are one line (a caption split in two, or two LRC lines on one timestamp).
    private static readonly TimeSpan SameMoment = TimeSpan.FromMilliseconds(50);

    private static readonly string[] MusicNotes = ["♪", "♫", "♬", "♩", "🎵", "🎶", "🎼"];

    /// <summary>
    /// Synced lines: ordered by start, trimmed, credit lines dropped, empty, music-note-only and section-header lines
    /// turned into breaks (they end the line before them), lines starting together joined, a caption repeated while it
    /// is still showing merged, and every end filled in (the source's own end, else the next line's start, else
    /// <paramref name="songEnd"/>), never past the next line's start. A line's end is taken as unknown when it is not
    /// after its start.
    /// </summary>
    public static IReadOnlyList<LyricLine> Timed(IEnumerable<LyricLine> lines, TimeSpan? songEnd, LyricsTextKind kind = LyricsTextKind.Lyrics)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var ordered = lines.Select((line, index) => (line, index))
            .OrderBy(x => x.line.Start)
            .ThenBy(x => x.index)
            .Select(x => (x.line, Text: CleanText(x.line.Text, kind)))
            .ToList();
        var sentenceCase = kind == LyricsTextKind.Captions && IsAllCaps(ordered.Select(x => x.Text));

        var drafts = new List<Draft>(ordered.Count);
        foreach (var (line, cleaned) in ordered)
        {
            var text = sentenceCase ? SentenceCase(cleaned) : cleaned;
            if (IsCredit(text))
            {
                continue;
            }

            var start = line.Start < TimeSpan.Zero ? TimeSpan.Zero : line.Start;
            var previous = drafts.Count > 0 ? drafts[^1] : null;
            if (text.Length == 0)
            {
                // A break: the line before it stops here.
                if (previous is not null && start > previous.Start && (previous.End is null || previous.End > start))
                {
                    previous.End = start;
                }

                continue;
            }

            TimeSpan? end = line.End > start ? line.End : null;

            // Word timings only stay when cleaning left the words' own text alone.
            var words = line.Words.Count > 0 && string.Concat(line.Words.Select(w => w.Text)).Trim() == text
                ? line.Words.ToList()
                : [];

            if (previous is not null && start - previous.Start <= SameMoment)
            {
                if (!SameText(previous.Text, text) && !previous.Text.EndsWith(" " + text, StringComparison.OrdinalIgnoreCase))
                {
                    previous.Words = previous.Words.Count > 0 && words.Count > 0
                        ? [.. previous.Words.SkipLast(1), previous.Words[^1] with { Text = previous.Words[^1].Text + " " }, .. words]
                        : [];
                    previous.Text += " " + text;
                }

                previous.End = Later(previous.End, end);
                continue;
            }

            if (previous is not null && SameText(previous.Text, text) && previous.End is { } shownUntil && start < shownUntil)
            {
                previous.End = Later(previous.End, end);
                continue;
            }

            drafts.Add(new Draft(start, end, text, words) { IsBackground = line.IsBackground });
        }

        var result = new List<LyricLine>(drafts.Count);
        for (var i = 0; i < drafts.Count; i++)
        {
            var draft = drafts[i];
            TimeSpan? next = i + 1 < drafts.Count ? drafts[i + 1].Start : null;
            var end = draft.End ?? next ?? (songEnd > draft.Start ? songEnd.Value : draft.Start + LastLineFallback);
            if (next is { } n && end > n)
            {
                end = n;
            }

            if (end <= draft.Start)
            {
                end = next is { } after && after > draft.Start ? after : draft.Start + LastLineFallback;
            }

            result.Add(new LyricLine(draft.Start, end, draft.Text, FitWords(draft.Words, draft.Start, end)) { IsBackground = draft.IsBackground });
        }

        return result;
    }

    /// <summary>
    /// Plain lyrics, one line per text line: trimmed, credit lines dropped, music notes and section headers
    /// ("[Chorus]") turned into verse breaks, at most one empty line in a row and none at either end.
    /// </summary>
    public static IReadOnlyList<LyricLine> Plain(string? text)
    {
        var result = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            var line = CleanText(raw, LyricsTextKind.Lyrics);
            if (IsCredit(line))
            {
                continue;
            }

            if (line.Length == 0)
            {
                if (result.Count > 0 && result[^1].Text.Length > 0)
                {
                    result.Add(new LyricLine(TimeSpan.Zero, TimeSpan.Zero, string.Empty, []));
                }

                continue;
            }

            result.Add(new LyricLine(TimeSpan.Zero, TimeSpan.Zero, line, []));
        }

        while (result.Count > 0 && result[^1].Text.Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }

    /// <summary>Lines without timing, from synced ones (for a plain version when the source only has synced lyrics).</summary>
    public static string ToPlainText(IEnumerable<LyricLine> lines) => string.Join('\n', lines.Select(l => l.Text));

    /// <summary>The writers named by credit lines ("Written by: …", "作词 : …"), or null when there are none.</summary>
    public static string? FindWriters(IEnumerable<string> lines)
    {
        var names = new List<string>();
        foreach (var line in lines)
        {
            var match = CreditRegex().Match(line.Trim());
            if (match.Success && IsWriterRole(match.Groups["role"].Value))
            {
                foreach (var name in NameSeparatorRegex().Split(match.Groups["names"].Value))
                {
                    var trimmed = name.Trim();
                    if (trimmed.Length > 0 && !names.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        names.Add(trimmed);
                    }
                }
            }
        }

        return names.Count == 0 ? null : string.Join(", ", names);
    }

    /// <summary>A line that is only a credit ("Composer: …", "作曲 : …"), not something sung.</summary>
    public static bool IsCredit(string line) => line.Length > 0 && CreditRegex().IsMatch(line);

    /// <summary>
    /// Trims the line, removes music notes around it, and empties it when it is only a section header ("[Chorus]") or,
    /// for captions, a sound description ("[Music]", "(upbeat music)"); captions also lose dialogue dashes.
    /// </summary>
    public static string CleanText(string? text, LyricsTextKind kind)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var line = WhitespaceRegex().Replace(text, " ").Trim();
        line = StripNotes(line);
        if (kind == LyricsTextKind.Captions)
        {
            line = DialogueDashRegex().Replace(line, string.Empty);
            line = LeadingBracketsRegex().Replace(StripNotes(line), string.Empty);
            line = TrailingBracketsRegex().Replace(line, string.Empty);
            line = StripNotes(line.Trim());
            if (line.StartsWith('(') && line.EndsWith(')') && SoundWordRegex().IsMatch(line))
            {
                return string.Empty;
            }
        }

        // Only brackets (a section header, or notes in brackets): nothing to sing.
        if (line.StartsWith('[') && line.EndsWith(']') && line.IndexOf(']', StringComparison.Ordinal) == line.Length - 1)
        {
            return string.Empty;
        }

        return line.Trim();
    }

    private static string StripNotes(string line)
    {
        bool changed;
        do
        {
            changed = false;
            foreach (var note in MusicNotes)
            {
                if (line.StartsWith(note, StringComparison.Ordinal))
                {
                    line = line[note.Length..].TrimStart();
                    changed = true;
                }

                if (line.EndsWith(note, StringComparison.Ordinal))
                {
                    line = line[..^note.Length].TrimEnd();
                    changed = true;
                }
            }
        }
        while (changed && line.Length > 0);

        return line;
    }

    private static bool IsAllCaps(IEnumerable<string> lines)
    {
        var any = false;
        foreach (var line in lines)
        {
            foreach (var c in line)
            {
                if (char.IsLower(c))
                {
                    return false;
                }

                any |= char.IsUpper(c);
            }
        }

        return any;
    }

    // "WE'RE NO STRANGERS TO LOVE" -> "We're no strangers to love" (and "I" stays a capital).
    private static string SentenceCase(string line)
    {
        if (line.Length == 0)
        {
            return line;
        }

        var lower = line.ToLower(CultureInfo.InvariantCulture);
        lower = PronounIRegex().Replace(lower, "I");
        var first = lower.AsSpan().IndexOfAnyExcept(" \"'(¿¡-");
        if (first < 0)
        {
            return lower;
        }

        var builder = new StringBuilder(lower);
        builder[first] = char.ToUpper(builder[first], CultureInfo.InvariantCulture);
        return builder.ToString();
    }

    private static bool SameText(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static TimeSpan? Later(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : (a > b ? a : b);

    private static bool IsWriterRole(string role)
    {
        var r = role.ToLowerInvariant();
        return !(r.Contains("arrang", StringComparison.Ordinal) || r.Contains("produc", StringComparison.Ordinal)
            || r.Contains("mix", StringComparison.Ordinal) || r.Contains("master", StringComparison.Ordinal)
            || r is "编曲" or "編曲" or "制作人" or "製作人" or "混音" or "母带");
    }

    /// <summary>Clips the words to the line and closes each open word at the next word's start (the last at the line's end).</summary>
    private static IReadOnlyList<LyricWord> FitWords(List<LyricWord> words, TimeSpan lineStart, TimeSpan lineEnd)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var fitted = new List<LyricWord>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            var start = word.Start < lineStart ? lineStart : word.Start > lineEnd ? lineEnd : word.Start;
            var end = word.End > word.Start ? word.End : i + 1 < words.Count ? words[i + 1].Start : lineEnd;
            if (end > lineEnd)
            {
                end = lineEnd;
            }

            if (end < start)
            {
                end = start;
            }

            fitted.Add(new LyricWord(start, end, word.Text));
        }

        return fitted;
    }

    private sealed class Draft(TimeSpan start, TimeSpan? end, string text, List<LyricWord> words)
    {
        public TimeSpan Start { get; } = start;

        public TimeSpan? End { get; set; } = end;

        public string Text { get; set; } = text;

        public List<LyricWord> Words { get; set; } = words;

        public bool IsBackground { get; init; }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"^(?:\[[^\]]*\]\s*)+")]
    private static partial Regex LeadingBracketsRegex();

    [GeneratedRegex(@"(?:\s*\[[^\]]*\])+$")]
    private static partial Regex TrailingBracketsRegex();

    [GeneratedRegex(@"^(?:>>\s*|-\s+|–\s*)")]
    private static partial Regex DialogueDashRegex();

    [GeneratedRegex(@"\b(?:music|instrumental|applause|cheer(?:s|ing)?|laugh(?:s|ing|ter)?|chuckles?|sighs?|vocali[sz]ing|humming|whistling|inaudible|silence|crowd)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SoundWordRegex();

    [GeneratedRegex(@"\bi\b(?=['’]|\s|$|[,.!?])")]
    private static partial Regex PronounIRegex();

    [GeneratedRegex(
        @"^(?<role>作词|作詞|作曲|编曲|編曲|制作人|製作人|混音|母带|词|詞|曲|lyrics(?:\s+by)?|lyricist|written\s+by|writers?|songwriters?|composer|composed\s+by|music(?:\s+by)?|words\s+by|arranger|arranged\s+by|producers?|produced\s+by|mixed\s+by|mastered\s+by)\s*[:：]\s*(?<names>\S.*)$",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreditRegex();

    [GeneratedRegex(@"\s*(?:[,/、&]|\band\b)\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NameSeparatorRegex();
}
