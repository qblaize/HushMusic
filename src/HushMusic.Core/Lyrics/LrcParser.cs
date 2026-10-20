using System.Globalization;
using System.Text;

namespace HushMusic.Core.Lyrics;

/// <summary>
/// A parsed LRC file. Lines are in time order; a line's <see cref="LyricLine.End"/> equals its start (not known yet)
/// unless a closing word tag gives it. <see cref="LyricsCleanup.Timed"/> fills in the rest.
/// </summary>
public sealed record LrcDocument(IReadOnlyList<LyricLine> Lines, bool HasWordTiming)
{
    /// <summary>ID tags such as <c>ar</c>, <c>ti</c>, <c>al</c>, <c>length</c> (keys in lower case).</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// LRC lyrics: <c>[mm:ss.xx]text</c> lines, several timestamps on one line, ID tags (<c>[ar:…]</c>, and
/// <c>[offset:±ms]</c>, which is applied), and enhanced LRC word tags (<c>&lt;mm:ss.xx&gt;word</c>).
/// </summary>
public static class LrcParser
{
    public static LrcDocument Parse(string? lrc)
    {
        var tags = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(lrc))
        {
            return new LrcDocument([], false) { Tags = tags };
        }

        var parsed = new List<LyricLine>();
        var hasWords = false;
        foreach (var rawLine in lrc.Split('\n'))
        {
            var line = rawLine.Trim();
            var times = new List<TimeSpan>();
            var position = 0;
            while (position < line.Length && line[position] == '[')
            {
                var close = line.IndexOf(']', position);
                if (close < 0)
                {
                    break;
                }

                var inner = line.AsSpan(position + 1, close - position - 1);
                if (TryParseTime(inner, out var time))
                {
                    times.Add(time);
                }
                else if (times.Count == 0 && TryParseTag(inner, out var key, out var value))
                {
                    tags[key] = value;
                }
                else
                {
                    break; // "[Chorus]" after the timestamps is text
                }

                position = close + 1;
            }

            if (times.Count == 0)
            {
                continue; // ID tag, comment or untimed text
            }

            var (text, words, end) = ParseWords(StripVoiceTag(line[position..]), times[0]);
            hasWords |= words.Count > 0;
            foreach (var start in times)
            {
                var shift = start - times[0];
                parsed.Add(new LyricLine(
                    start,
                    end is { } e ? e + shift : start,
                    text,
                    shift == TimeSpan.Zero ? words : [.. words.Select(w => w with { Start = w.Start + shift, End = w.End + shift })]));
            }
        }

        var offset = tags.TryGetValue("offset", out var offsetText)
            && int.TryParse(offsetText.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offsetMs)
                ? TimeSpan.FromMilliseconds(offsetMs)
                : TimeSpan.Zero;

        // A positive offset shows the lyrics earlier. Sorting is stable: lines with the same time keep file order.
        var lines = parsed
            .Select(l => offset == TimeSpan.Zero ? l : Shift(l, -offset))
            .OrderBy(l => l.Start)
            .ToList();
        return new LrcDocument(lines, hasWords) { Tags = tags };
    }

    /// <summary><c>mm:ss</c>, <c>mm:ss.f</c>, <c>mm:ss.ff</c>, <c>mm:ss.fff</c> or <c>mm:ss:ff</c> (minutes may exceed 59).</summary>
    public static bool TryParseTime(ReadOnlySpan<char> text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        text = text.Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0 || !AllDigits(text[..colon]))
        {
            return false;
        }

        var rest = text[(colon + 1)..];
        var separator = rest.IndexOfAny('.', ':');
        var secondsText = separator < 0 ? rest : rest[..separator];
        var fractionText = separator < 0 ? ReadOnlySpan<char>.Empty : rest[(separator + 1)..];
        if (secondsText.Length is < 1 or > 2 || !AllDigits(secondsText) || fractionText.Length > 3
            || (separator >= 0 && (fractionText.Length == 0 || !AllDigits(fractionText))))
        {
            return false;
        }

        if (!int.TryParse(text[..colon], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        var seconds = int.Parse(secondsText, NumberStyles.None, CultureInfo.InvariantCulture);

        // ".5" is half a second, ".05" five hundredths: the fraction is read as a decimal.
        var milliseconds = 0;
        for (var i = 0; i < 3; i++)
        {
            milliseconds = (milliseconds * 10) + (i < fractionText.Length ? fractionText[i] - '0' : 0);
        }

        time = new TimeSpan(0, 0, minutes, seconds, milliseconds);
        return true;
    }

    private static bool TryParseTag(ReadOnlySpan<char> inner, out string key, out string value)
    {
        key = value = string.Empty;
        var colon = inner.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        var name = inner[..colon].Trim();
        foreach (var c in name)
        {
            if (!char.IsAsciiLetter(c))
            {
                return false;
            }
        }

        key = name.ToString().ToLowerInvariant();
        value = inner[(colon + 1)..].Trim().ToString();
        return true;
    }

    /// <summary>Splits enhanced LRC word tags. Without tags the words list is empty and the end unknown.</summary>
    private static (string Text, IReadOnlyList<LyricWord> Words, TimeSpan? End) ParseWords(string text, TimeSpan lineStart)
    {
        if (!text.Contains('<', StringComparison.Ordinal))
        {
            return (text.Trim(), [], null);
        }

        var words = new List<LyricWord>();
        var current = new StringBuilder();
        var wordStart = lineStart;
        TimeSpan? lastTag = null;
        var endsWithTag = false;
        var position = 0;
        while (position < text.Length)
        {
            var close = text[position] == '<' ? text.IndexOf('>', position) : -1;
            if (close > position && TryParseTime(text.AsSpan(position + 1, close - position - 1), out var tag))
            {
                Flush(tag);
                wordStart = tag;
                lastTag = tag;
                endsWithTag = true;
                position = close + 1;
                continue;
            }

            current.Append(text[position]);
            endsWithTag = false;
            position++;
        }

        Flush(null);
        if (lastTag is null)
        {
            return (text.Trim(), [], null);
        }

        if (words.Count > 0)
        {
            words[^1] = words[^1] with { Text = words[^1].Text.TrimEnd() };
        }

        var joined = string.Concat(words.Select(w => w.Text)).Trim();
        return (joined, words, endsWithTag ? lastTag : null);

        // Closes the word collected so far. Whitespace between two tags belongs to the previous word.
        void Flush(TimeSpan? end)
        {
            var piece = current.ToString();
            current.Clear();
            if (string.IsNullOrWhiteSpace(piece))
            {
                if (piece.Length > 0 && words.Count > 0)
                {
                    words[^1] = words[^1] with { Text = words[^1].Text + piece };
                }

                return;
            }

            if (words.Count == 0)
            {
                piece = piece.TrimStart();
            }

            words.Add(new LyricWord(wordStart, end ?? wordStart, piece));
        }
    }

    // Enhanced LRC marks duet voices with "v1:", "v2:" at the start of the text.
    private static string StripVoiceTag(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length > 2 && (trimmed[0] == 'v' || trimmed[0] == 'V'))
        {
            var colon = trimmed.IndexOf(':');
            if (colon > 1 && colon <= 3 && AllDigits(trimmed.AsSpan(1, colon - 1)))
            {
                return trimmed[(colon + 1)..];
            }
        }

        return text;
    }

    private static LyricLine Shift(LyricLine line, TimeSpan by)
    {
        static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
        return line with
        {
            Start = Clamp(line.Start + by),
            End = Clamp(line.End + by),
            Words = [.. line.Words.Select(w => w with { Start = Clamp(w.Start + by), End = Clamp(w.End + by) })],
        };
    }

    private static bool AllDigits(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return text.Length > 0;
    }
}
