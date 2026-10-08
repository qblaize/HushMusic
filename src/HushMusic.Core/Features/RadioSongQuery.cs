using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HushMusic.Core.Features;

/// <summary>
/// Turns the song a station announces in its ICY title into YouTube Music search text. Stations decorate titles in many
/// ways: "(Radio Edit)", "[Original Mix]", their own name or web address, "Now playing:", "| Deep House", stars and
/// quotes. A search for the plain "Artist Title" finds the song far more reliably.
/// </summary>
public static partial class RadioSongQuery
{
    // Bracketed tags that describe the edit or the upload, not the song. A remix by name, "Live" or "Acoustic" stay:
    // they pick the right version.
    private static readonly HashSet<string> NoiseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "radio edit", "radio mix", "radio version", "radio cut", "original mix", "original version", "extended mix",
        "extended version", "extended edit", "extended", "club mix", "club edit", "edit", "clean", "clean version",
        "explicit", "explicit version", "dirty", "single version", "single edit", "album version", "short edit",
        "short version", "official video", "official music video", "official audio", "music video", "video", "audio",
        "lyrics", "lyric video", "hq", "hd", "new", "premiere", "free download", "out now", "remastered", "remaster",
    };

    // Compilations announce "Various - Artist/Title": the real artist is in the title part.
    private static readonly HashSet<string> PlaceholderArtists = new(StringComparer.Ordinal)
    {
        "various", "variousartists", "va", "unknown", "unknownartist",
    };

    /// <summary>
    /// "Artist Title" for searching, cleaned of station decorations. <paramref name="artist"/> may be null (titles without
    /// " - "). Returns an empty string when nothing searchable is left.
    /// </summary>
    public static string Build(string? artist, string title, string? stationName = null)
    {
        ArgumentNullException.ThrowIfNull(title);
        var cleanTitle = Clean(title, stationName);
        var cleanArtist = artist is null || IsPlaceholderArtist(artist) ? string.Empty : Clean(artist, stationName);
        if (cleanArtist.Length > 0 && cleanTitle.Length > 0 && !string.Equals(Comparable(cleanArtist), Comparable(cleanTitle), StringComparison.Ordinal))
        {
            return cleanArtist + " " + cleanTitle;
        }

        return cleanTitle.Length > 0 ? cleanTitle : cleanArtist;
    }

    /// <summary>"Various", "Various Artists", "VA", "Unknown": not a real artist.</summary>
    public static bool IsPlaceholderArtist(string? artist) => PlaceholderArtists.Contains(Comparable(artist));

    /// <summary>One part of a stream title (the artist or the song) without decorations, tags and extra whitespace.</summary>
    public static string Clean(string text, string? stationName = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        var value = Whitespace().Replace(text, " ").Trim();

        // "Now playing: …", "On air - …"
        value = LeadingAnnouncement().Replace(value, string.Empty);

        // A trailing station tag or genre after a bar, double slash or "@": "Song | Deep House", "Song // Radio X".
        value = TrailingTag().Replace(value, string.Empty);

        value = WebAddress().Replace(value, " ");

        // "Artist/Title", "Artist \ Title": a slash joins two parts, it isn't part of either.
        value = value.Replace('/', ' ').Replace('\\', ' ');
        value = SquareOrCurly().Replace(value, " ");
        value = Parenthesised().Replace(value, match => NoiseTags.Contains(NormalizeTag(match.Groups[1].Value)) ? " " : match.Value);

        // Stations with a fixed-length title field cut it off mid-bracket: "Song (feat. Some".
        value = DanglingBracket().Replace(value, string.Empty);
        value = Decoration().Replace(value, " ");

        if (stationName is { } station && Comparable(station).Length >= 4)
        {
            value = Regex.Replace(value, Regex.Escape(station.Trim()), " ", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        }

        value = Whitespace().Replace(value, " ");
        return value.Trim(' ', '-', '–', '—', '|', '/', ':', ';', ',', '.', '~', '·', '•', '+');
    }

    /// <summary>Lower-case letters and digits only, without diacritics: for comparing names loosely.</summary>
    public static string Comparable(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(c));
            }
        }

        return builder.ToString();
    }

    // "2011 Remaster", "Remastered 2011", "2011": a year tag is a noise tag too.
    private static string NormalizeTag(string tag)
    {
        var value = Whitespace().Replace(tag, " ").Trim().TrimEnd('!');
        value = YearWord().Replace(value, string.Empty).Trim();
        return value.Length == 0 ? "remastered" : value;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^(?:now\s+playing|now\s+on\s+air|on\s+air|playing\s+now|currently\s+playing|np)\s*[:\-–—|]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeadingAnnouncement();

    [GeneratedRegex(@"\s+(?:\||//|@|•|~)\s+.*$")]
    private static partial Regex TrailingTag();

    [GeneratedRegex(@"(?:https?://|www\.)\S+|\b[\w-]+\.(?:com|net|org|fm|radio|online|stream|live)\b\S*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex WebAddress();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex SquareOrCurly();

    [GeneratedRegex(@"\(([^()]*)\)")]
    private static partial Regex Parenthesised();

    [GeneratedRegex(@"\s*[(\[][^)\]]*$")]
    private static partial Regex DanglingBracket();

    [GeneratedRegex(@"[♪♫♬★☆*""“”«»„]+")]
    private static partial Regex Decoration();

    [GeneratedRegex(@"\b(?:19|20)\d{2}\b")]
    private static partial Regex YearWord();
}
