using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using HushMusic.Core.Models;

namespace HushMusic.InnerTube.Parsing.Common;

/// <summary>What <see cref="TextRuns.ParseSongRuns"/> found in a subtitle/byline run list.</summary>
internal sealed record SongRunsInfo(
    IReadOnlyList<ArtistRef> Artists,
    AlbumRef? Album,
    string? Views,
    string? DurationText,
    string? Year)
{
    public TimeSpan? Duration => TextRuns.ParseDuration(DurationText);
}

/// <summary>
/// Helpers for InnerTube "runs" (<c>{"runs": [{"text": ..., "navigationEndpoint"?: ...}]}</c>), ported from
/// ytmusicapi parsers/songs.py, parsers/_utils.py and parsers/artists.py.
/// </summary>
internal static partial class TextRuns
{
    private const string Dot = " \u2022 ";

    /// <summary>Concatenated text of <c>holder.runs[*].text</c>; null when there are no runs.</summary>
    public static string? Text(JsonNode? holder)
    {
        if (holder.Arr("runs") is not { Count: > 0 } runs)
        {
            return holder.Str("simpleText");
        }

        var builder = new StringBuilder();
        foreach (var run in runs)
        {
            builder.Append(run.Str("text"));
        }

        return builder.ToString();
    }

    /// <summary>The " • " separator run (ytmusicapi compares the whole run object to <c>{"text": " • "}</c>).</summary>
    public static bool IsDot(JsonNode? run) => run is JsonObject { Count: 1 } obj && obj.Str("text") == Dot;

    public static bool HasLink(JsonNode? run) => run.Has("navigationEndpoint");

    public static string? BrowseId(JsonNode? run) => run.Str("navigationEndpoint", "browseEndpoint", "browseId");

    /// <summary>"4:36" -> 276 s, "1:27:54" -> 5274 s. Anything that is not digits separated by ':' -> null.</summary>
    public static TimeSpan? ParseDuration(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var parts = text.Trim().Split(':');
        if (parts.Length > 3 || parts.Any(p => p.Length is 0 or > 6 || !p.All(char.IsAsciiDigit)))
        {
            return null;
        }

        long seconds = 0;
        foreach (var part in parts)
        {
            seconds = (seconds * 60) + long.Parse(part, CultureInfo.InvariantCulture);
        }

        return TimeSpan.FromSeconds(seconds);
    }

    public static bool IsDurationText(string text) => DurationRegex().IsMatch(text);

    /// <summary>
    /// True when the text is a view/play count ("52M plays", "1.2B views"). Mirrors ytmusicapi parse_views:
    /// it must start with a digit, and a bare ASCII token like "2Pac" is an artist name, not a count.
    /// </summary>
    public static bool IsViewsText(string text)
    {
        var prefixed = false;
        if (!LatinLetterRegex().IsMatch(text))
        {
            // Non-Latin locales put a word before the number ("조회수 17억회").
            var stripped = ViewsPrefixRegex().Replace(text, string.Empty, 1);
            prefixed = stripped.Length != text.Length;
            text = stripped;
        }

        if (text.Length == 0 || !char.IsAsciiDigit(text[0]))
        {
            return false;
        }

        return prefixed || !text.All(char.IsAscii) || text.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>All digits of the text as an int ("13 songs" -> 13, "1,234" -> 1234). Null when there are none.</summary>
    public static int? ParseInt(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var digits = new string(text.Where(char.IsAsciiDigit).ToArray());
        return digits.Length > 0 && int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>First space-separated token ("7.19M subscribers" -> "7.19M").</summary>
    public static string? FirstToken(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim().Split(' ')[0];

    /// <summary>
    /// ytmusicapi parse_artists_runs: every even run is an artist (odd runs are ", " / " & " separators).
    /// </summary>
    public static IReadOnlyList<ArtistRef> ParseArtistsRuns(IEnumerable<JsonNode?>? runs)
    {
        if (runs is null)
        {
            return [];
        }

        var artists = new List<ArtistRef>();
        var index = 0;
        foreach (var run in runs)
        {
            if (index++ % 2 == 0 && run.Str("text") is { Length: > 0 } name)
            {
                artists.Add(new ArtistRef(name, BrowseId(run)));
            }
        }

        return artists;
    }

    /// <summary>
    /// ytmusicapi parse_song_runs: classifies the even runs of a subtitle into artists, album, views,
    /// duration and year. With <paramref name="skipTypeSpec"/> a leading unlinked type word
    /// ("Song", "Video", "Single"...) followed by " • " is dropped.
    /// </summary>
    /// <remarks>
    /// Views keep the full display text ("52M plays"); ytmusicapi keeps only the first token ("52M").
    /// </remarks>
    public static SongRunsInfo ParseSongRuns(IList<JsonNode?>? runs, bool skipTypeSpec = false)
    {
        var artists = new List<ArtistRef>();
        AlbumRef? album = null;
        string? views = null, duration = null, year = null;
        if (runs is null)
        {
            return new SongRunsInfo(artists, album, views, duration, year);
        }

        var start = 0;
        if (skipTypeSpec
            && runs.Count > 2
            && !HasLink(runs[0])
            && Classify(runs[0]) == RunKind.Artist
            && IsDot(runs[1])
            && Classify(runs[2]) is RunKind.Artist or RunKind.Duration or RunKind.Views or RunKind.Year)
        {
            start = 2;
        }

        for (var i = start; i < runs.Count; i += 2)
        {
            var run = runs[i];
            var text = run.Str("text") ?? string.Empty;
            switch (Classify(run))
            {
                case RunKind.Album:
                    album = new AlbumRef(text, BrowseId(run));
                    break;
                case RunKind.Duration:
                    duration = text;
                    break;
                case RunKind.Year:
                    year = text;
                    break;
                case RunKind.Views:
                    views = text;
                    break;
                case RunKind.Artist when !string.IsNullOrWhiteSpace(text):
                    artists.Add(new ArtistRef(text, BrowseId(run)));
                    break;
            }
        }

        return new SongRunsInfo(artists, album, views, duration, year);
    }

    /// <summary><see cref="ParseSongRuns"/> over <c>runs[skip..]</c>.</summary>
    public static SongRunsInfo ParseSongRuns(JsonArray? runs, int skip, bool skipTypeSpec = false) =>
        ParseSongRuns(runs is null ? null : runs.Skip(skip).ToList(), skipTypeSpec);

    private static RunKind Classify(JsonNode? run)
    {
        if (HasLink(run))
        {
            return BrowseIds.IsAlbum(BrowseId(run)) ? RunKind.Album : RunKind.Artist;
        }

        var text = run.Str("text") ?? string.Empty;
        if (DurationRegex().IsMatch(text))
        {
            return RunKind.Duration;
        }

        if (YearRegex().IsMatch(text))
        {
            return RunKind.Year;
        }

        return IsViewsText(text) ? RunKind.Views : RunKind.Artist;
    }

    private enum RunKind
    {
        Artist,
        Album,
        Duration,
        Year,
        Views,
    }

    [GeneratedRegex(@"^(\d+:)*\d+:\d+$")]
    private static partial Regex DurationRegex();

    [GeneratedRegex(@"^\d{4}$")]
    private static partial Regex YearRegex();

    [GeneratedRegex("[a-zA-Z]")]
    private static partial Regex LatinLetterRegex();

    [GeneratedRegex(@"^\D*?[\s:\uff1a\u200e-\u200f\u202a-\u202e]")]
    private static partial Regex ViewsPrefixRegex();
}
