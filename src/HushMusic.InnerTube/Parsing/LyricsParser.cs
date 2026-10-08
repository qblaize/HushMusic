using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// Lyrics, <c>browse {"browseId": "MPLY..."}</c> (ytmusicapi get_lyrics). Accepts both the plain WEB_REMIX
/// response and the timed one returned to the ANDROID_MUSIC client.
/// </summary>
internal static class LyricsParser
{
    private const string Page = "Lyrics";

    /// <summary>Returns null when the response holds no lyrics.</summary>
    public static Lyrics? Parse(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);

        var timed = response.Obj("contents", "elementRenderer", "newElement", "type", "componentType", "model", "timedLyricsModel", "lyricsData");
        if (timed is not null)
        {
            return ParseTimed(timed, scope);
        }

        var shelf = response.Arr("contents", "sectionListRenderer", "contents")
            .Objects()
            .Select(s => s.Obj("musicDescriptionShelfRenderer"))
            .FirstOrDefault(s => s is not null);
        var text = TextRuns.Text(shelf.Obj("description"));
        if (string.IsNullOrEmpty(text))
        {
            scope.IgnoreItem(response.Obj("contents").Describe(), "no lyrics in response");
            return null;
        }

        return new Lyrics
        {
            Text = text,
            // ytmusicapi reads musicDescriptionShelfRenderer.runs[0].text, which does not exist (always None);
            // "Source: LyricFind" is in the footer.
            Source = TextRuns.Text(shelf.Obj("footer")) ?? shelf.Str("runs", 0, "text"),
        };
    }

    private static Lyrics? ParseTimed(JsonObject data, ParseScope scope)
    {
        var entries = data.Arr("timedLyricsData");
        if (entries is null || entries.Count == 0)
        {
            scope.IgnoreItem("timedLyricsModel", "no timedLyricsData");
            return null;
        }

        var lines = new List<TimedLyricLine>(entries.Count);
        var allTimed = true;
        foreach (var entry in entries.Objects())
        {
            var lineText = entry.Str("lyricLine") ?? string.Empty;
            // The millisecond values arrive as JSON strings ("22150"); they are converted to TimeSpans here.
            var start = entry.Long("cueRange", "startTimeMilliseconds");
            var end = entry.Long("cueRange", "endTimeMilliseconds");
            if (start is null || end is null)
            {
                allTimed = false;
                lines.Add(new TimedLyricLine(TimeSpan.Zero, TimeSpan.Zero, lineText));
                continue;
            }

            lines.Add(new TimedLyricLine(TimeSpan.FromMilliseconds(start.Value), TimeSpan.FromMilliseconds(end.Value), lineText));
        }

        return new Lyrics
        {
            Text = string.Join('\n', lines.Select(l => l.Text)),
            Source = data.Str("sourceMessage"),
            // ytmusicapi falls back to plain lyrics when any line lacks its cue range.
            TimedLines = allTimed ? lines : null,
        };
    }
}
