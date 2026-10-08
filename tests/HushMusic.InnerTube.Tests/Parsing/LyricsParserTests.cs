using System.Text.Json.Nodes;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class LyricsParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Plain_lyrics_keep_CRLF_and_read_the_source_from_the_footer()
    {
        var lyrics = LyricsParser.Parse(ParserFixtures.Load("lyrics.json"), _log);

        Assert.NotNull(lyrics);
        Assert.StartsWith("Today is gonna be the day\r\nThat they're gonn", lyrics.Text);
        Assert.EndsWith("onna be the one that saves me (saves me)", lyrics.Text);
        Assert.Equal(1432, lyrics.Text.Length);
        Assert.Equal(45, lyrics.Text.Split("\r\n").Length);
        // ytmusicapi reads a path that does not exist and returns None.
        Assert.Equal("Source: LyricFind", lyrics.Source);
        Assert.Null(lyrics.TimedLines);
    }

    [Fact]
    public void Timed_lyrics()
    {
        var lyrics = LyricsParser.Parse(ParserFixtures.Load("lyrics_timed.json"), _log);

        Assert.NotNull(lyrics);
        Assert.Equal("Source: LyricFind", lyrics.Source);
        var lines = lyrics.TimedLines;
        Assert.NotNull(lines);
        Assert.Equal(40, lines.Count);
        Assert.Equal(("♪", TimeSpan.Zero, TimeSpan.FromMilliseconds(22150)), (lines[0].Text, lines[0].Start, lines[0].End));
        Assert.Equal(("Today is gonna be the day", TimeSpan.FromMilliseconds(22150), TimeSpan.FromMilliseconds(24420)), (lines[1].Text, lines[1].Start, lines[1].End));
        Assert.Equal(
            ("You're gonna be the one that saves me (saves me)", TimeSpan.FromMilliseconds(217380), TimeSpan.FromMilliseconds(222950)),
            (lines[^1].Text, lines[^1].Start, lines[^1].End));
        Assert.Equal(string.Join('\n', lines.Select(l => l.Text)), lyrics.Text);
    }

    [Fact]
    public void Timed_lyrics_with_a_missing_cue_range_fall_back_to_plain_text()
    {
        var response = ParserFixtures.Load("lyrics_timed.json");
        response.At("contents", "elementRenderer", "newElement", "type", "componentType", "model", "timedLyricsModel", "lyricsData",
            "timedLyricsData", 3).AsObject().Remove("cueRange");

        var lyrics = LyricsParser.Parse(response, _log);

        Assert.NotNull(lyrics);
        Assert.Null(lyrics.TimedLines);
        Assert.Equal(40, lyrics.Text.Split('\n').Length);
    }

    [Fact]
    public void Unsynced_lyrics_from_the_mobile_client_have_no_cue_ranges_and_become_plain_text()
    {
        // ANDROID_MUSIC answers every lyrics browse with a timedLyricsModel; unsynced lyrics just lack cueRange.
        var lyrics = LyricsParser.Parse(ParserFixtures.Load("lyrics_timed_unsynced.json"), _log);

        Assert.NotNull(lyrics);
        Assert.Null(lyrics.TimedLines);
        Assert.Equal("Source: Musixmatch", lyrics.Source);
        var lines = lyrics.Text.Split('\n');
        Assert.Equal(16, lines.Length);
        Assert.Equal(("Pana cand nu te iubeam,", "Dorule, dorule!"), (lines[0], lines[^1]));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Timed_model_without_lines_returns_null()
    {
        var response = ParserFixtures.Load("lyrics_timed.json");
        response.Delete("timedLyricsData", "contents", "elementRenderer", "newElement", "type", "componentType", "model", "timedLyricsModel", "lyricsData");

        Assert.Null(LyricsParser.Parse(response, _log));
    }

    [Fact]
    public void Response_without_lyrics_returns_null()
    {
        var response = ParserFixtures.Load("lyrics.json");
        response.At("contents", "sectionListRenderer", "contents", 0, "musicDescriptionShelfRenderer").AsObject().Remove("description");

        Assert.Null(LyricsParser.Parse(response, _log));
        Assert.Null(LyricsParser.Parse(new JsonObject(), _log));
    }
}
