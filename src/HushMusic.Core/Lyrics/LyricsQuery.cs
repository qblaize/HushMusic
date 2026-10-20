using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Lyrics;

/// <summary>What a lyrics database is asked for: a cleaned-up title, the first artist, the album and the length.</summary>
public sealed record LyricsQuery(string Title, string Artist, string? Album, TimeSpan? Duration)
{
    /// <summary>
    /// The query for a track: "(Official Video)", "[Lyrics]", "feat." parts and the like are removed from the title,
    /// and an "Artist - " prefix (common in video titles) when it names the artist. Null when the track has no artist.
    /// </summary>
    public static LyricsQuery? From(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        var artist = track.Artists.Select(a => LyricsText.CleanArtist(a.Name)).FirstOrDefault(a => a.Length > 0);
        if (artist is null)
        {
            return null;
        }

        var title = LyricsText.CleanTitle(track.Title, artist);
        if (title.Length == 0)
        {
            return null;
        }

        var album = track.Album?.Name is { Length: > 0 } name ? name.Trim() : null;
        return new LyricsQuery(title, artist, album, track.Duration is { } d && d > TimeSpan.Zero ? d : null);
    }
}

/// <summary>Title and artist clean-up and fuzzy comparison for matching a track against lyrics databases.</summary>
public static partial class LyricsText
{
    /// <summary>
    /// The song title without video and promo noise: bracketed parts about the video or audio ("(Official Music
    /// Video)", "[HD]", "(Lyrics)"), "feat." credits, a " | …" tail, surrounding quotes, and an "Artist - " prefix
    /// that names <paramref name="artist"/>. Version notes such as "(Live)" or "(Remastered 2011)" stay.
    /// </summary>
    public static string CleanTitle(string title, string? artist = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var text = WhitespaceRegex().Replace(title, " ").Trim();

        if (artist is { Length: > 0 } && DashRegex().Match(text) is { Success: true } dash)
        {
            var left = text[..dash.Index];
            var right = text[(dash.Index + dash.Length)..];
            if (right.Length > 0 && SameArtist(left, artist))
            {
                text = right;
            }
        }

        text = FeatBracketRegex().Replace(text, string.Empty);
        text = NoiseBracketRegex().Replace(text, string.Empty);
        text = FeatTailRegex().Replace(text, string.Empty);
        text = PipeTailRegex().Replace(text, string.Empty);
        text = WhitespaceRegex().Replace(text, " ").Trim();
        if (text.Length > 2 && QuoteChars.Contains(text[0]) && QuoteChars.Contains(text[^1]))
        {
            text = text[1..^1].Trim();
        }

        return text.Length > 0 ? text : title.Trim();
    }

    /// <summary>
    /// The bare title for searching and comparing: <see cref="CleanTitle"/> without any bracketed part or " - …" tail
    /// (so "Song (Remastered 2011)" and "Song - Live" both become "Song").
    /// </summary>
    public static string CoreTitle(string title)
    {
        var clean = CleanTitle(title);
        var core = AnyBracketRegex().Replace(clean, string.Empty);
        core = DashTailRegex().Replace(core, string.Empty);
        core = WhitespaceRegex().Replace(core, " ").Trim();
        return core.Length > 0 ? core : clean;
    }

    /// <summary>The artist name without channel suffixes (" - Topic", "VEVO").</summary>
    public static string CleanArtist(string artist)
    {
        var text = WhitespaceRegex().Replace(artist ?? string.Empty, " ").Trim();
        text = TopicRegex().Replace(text, string.Empty);
        if (text.Length > 4 && text.EndsWith("VEVO", StringComparison.Ordinal))
        {
            text = text[..^4];
        }

        return text.Trim();
    }

    /// <summary>Lower case, accents removed, punctuation turned into spaces, "&amp;" read as "and".</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var decomposed = text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(decomposed.Length);
        var space = true;
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(c))
            {
                builder.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (c is not '\'' and not '’' && !space)
            {
                builder.Append(' ');
                space = true;
            }
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Dice coefficient of the two texts' letter pairs, after <see cref="Normalize"/>: 1 for the same text, 0 for
    /// nothing in common.
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        var x = Normalize(a).Replace(" ", string.Empty, StringComparison.Ordinal);
        var y = Normalize(b).Replace(" ", string.Empty, StringComparison.Ordinal);
        if (x == y)
        {
            return x.Length > 0 ? 1 : 0;
        }

        if (x.Length < 2 || y.Length < 2)
        {
            return 0;
        }

        var pairs = new Dictionary<(char, char), int>();
        for (var i = 0; i < x.Length - 1; i++)
        {
            var pair = (x[i], x[i + 1]);
            pairs[pair] = pairs.GetValueOrDefault(pair) + 1;
        }

        var matches = 0;
        for (var i = 0; i < y.Length - 1; i++)
        {
            var pair = (y[i], y[i + 1]);
            if (pairs.GetValueOrDefault(pair) is > 0 and var count)
            {
                pairs[pair] = count - 1;
                matches++;
            }
        }

        return 2.0 * matches / (x.Length - 1 + y.Length - 1);
    }

    /// <summary>
    /// The credited artist <paramref name="candidate"/> includes <paramref name="artist"/> ("Luis Fonsi, Daddy Yankee"
    /// for "Luis Fonsi"), or the two are near enough the same name.
    /// </summary>
    public static bool SameArtist(string candidate, string artist)
    {
        var c = Normalize(candidate);
        var a = Normalize(CleanArtist(artist));
        if (c.Length == 0 || a.Length == 0)
        {
            return false;
        }

        return c == a
            || (" " + c + " ").Contains(" " + a + " ", StringComparison.Ordinal)
            || (" " + a + " ").Contains(" " + c + " ", StringComparison.Ordinal)
            || Similarity(c, a) >= 0.8;
    }

    private const string QuoteChars = "\"“”'‘’«»";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    // "Artist - Title": a spaced hyphen or dash.
    [GeneratedRegex(@"\s+[-–—]\s+")]
    private static partial Regex DashRegex();

    [GeneratedRegex(@"\s+[-–—]\s+.*$")]
    private static partial Regex DashTailRegex();

    [GeneratedRegex(@"\s*[\(\[【]\s*(?:feat\.?|ft\.?|featuring|with)\s[^\)\]】]*[\)\]】]", RegexOptions.IgnoreCase)]
    private static partial Regex FeatBracketRegex();

    [GeneratedRegex(@"\s+(?:feat\.?|ft\.|featuring)\s+.*$", RegexOptions.IgnoreCase)]
    private static partial Regex FeatTailRegex();

    [GeneratedRegex(
        @"\s*[\(\[【][^\)\]】]*\b(?:official|video|videoclip|clip|audio|lyrics?|letra|paroles|visuali[sz]er|hd|hq|4k|8k|1080p|720p|mv|m/v|explicit|color\s+coded)\b[^\)\]】]*[\)\]】]",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseBracketRegex();

    [GeneratedRegex(@"\s*[\(\[【][^\)\]】]*[\)\]】]")]
    private static partial Regex AnyBracketRegex();

    [GeneratedRegex(@"\s+[|｜].*$")]
    private static partial Regex PipeTailRegex();

    [GeneratedRegex(@"\s*-\s*Topic$", RegexOptions.IgnoreCase)]
    private static partial Regex TopicRegex();
}
