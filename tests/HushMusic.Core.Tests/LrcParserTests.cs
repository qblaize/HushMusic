using HushMusic.Core.Lyrics;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LrcParserTests
{
    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Lines_are_timed_and_their_end_is_left_open()
    {
        var lrc = LrcParser.Parse("[00:12.34]Hello there\r\n[00:15.00] World \n\n");

        Assert.Equal(2, lrc.Lines.Count);
        Assert.Equal(Ms(12_340), lrc.Lines[0].Start);
        Assert.Equal(lrc.Lines[0].Start, lrc.Lines[0].End);
        Assert.Equal("Hello there", lrc.Lines[0].Text);
        Assert.Equal("World", lrc.Lines[1].Text);
        Assert.Empty(lrc.Lines[0].Words);
        Assert.False(lrc.HasWordTiming);
    }

    [Theory]
    [InlineData("01:02", 62_000)]
    [InlineData("01:02.5", 62_500)]
    [InlineData("01:02.05", 62_050)]
    [InlineData("01:02.050", 62_050)]
    [InlineData("01:02:50", 62_500)]
    [InlineData("123:00.00", 7_380_000)]
    [InlineData(" 00:07.10 ", 7_100)]
    public void Timestamps_in_every_common_form_are_read(string text, int milliseconds)
    {
        Assert.True(LrcParser.TryParseTime(text, out var time));
        Assert.Equal(Ms(milliseconds), time);
    }

    [Theory]
    [InlineData("ar:Someone")]
    [InlineData("00:1x")]
    [InlineData("00:123")]
    [InlineData(":12")]
    [InlineData("00:12.")]
    [InlineData("00:12.3456")]
    public void Other_tags_are_not_timestamps(string text)
    {
        Assert.False(LrcParser.TryParseTime(text, out _));
    }

    [Fact]
    public void A_line_with_several_timestamps_repeats_in_time_order()
    {
        var lrc = LrcParser.Parse("[00:10.00][00:30.00]Chorus\n[00:20.00]Verse");

        Assert.Equal(["Chorus", "Verse", "Chorus"], lrc.Lines.Select(l => l.Text));
        Assert.Equal([Ms(10_000), Ms(20_000), Ms(30_000)], lrc.Lines.Select(l => l.Start));
    }

    [Fact]
    public void Id_tags_are_kept_and_the_offset_applied()
    {
        var lrc = LrcParser.Parse("[ar: Some Artist]\n[ti:Song]\n[offset:+500]\n[00:10.00]Line\n[00:00.20]Early");

        Assert.Equal("Some Artist", lrc.Tags["ar"]);
        Assert.Equal("Song", lrc.Tags["ti"]);

        // A positive offset shows the lyrics earlier; nothing goes before zero.
        Assert.Equal([TimeSpan.Zero, Ms(9_500)], lrc.Lines.Select(l => l.Start));
    }

    [Fact]
    public void Enhanced_word_tags_time_each_word()
    {
        var lrc = LrcParser.Parse("[00:10.00]<00:10.00>Hello <00:10.50>big <00:11.00>world<00:12.00>");

        Assert.True(lrc.HasWordTiming);
        var line = Assert.Single(lrc.Lines);
        Assert.Equal("Hello big world", line.Text);
        Assert.Equal(Ms(12_000), line.End);
        Assert.Equal(
            [new LyricWord(Ms(10_000), Ms(10_500), "Hello "), new LyricWord(Ms(10_500), Ms(11_000), "big "), new LyricWord(Ms(11_000), Ms(12_000), "world")],
            line.Words);
    }

    [Fact]
    public void Text_before_the_first_word_tag_starts_with_the_line()
    {
        var line = Assert.Single(LrcParser.Parse("[00:10.00]Hello <00:10.50>world").Lines);

        Assert.Equal(new LyricWord(Ms(10_000), Ms(10_500), "Hello "), line.Words[0]);

        // The last word has no closing tag: its end is still open.
        Assert.Equal(new LyricWord(Ms(10_500), Ms(10_500), "world"), line.Words[1]);
        Assert.Equal(line.Start, line.End);
    }

    [Fact]
    public void Word_times_follow_each_timestamp_of_a_repeated_line()
    {
        var lrc = LrcParser.Parse("[00:10.00][00:20.00]<00:10.00>La <00:10.50>la<00:11.00>");

        Assert.Equal(Ms(20_500), lrc.Lines[1].Words[1].Start);
        Assert.Equal(Ms(21_000), lrc.Lines[1].End);
    }

    [Fact]
    public void Voice_markers_section_headers_and_stray_angle_brackets()
    {
        var lrc = LrcParser.Parse("[00:01.00]v1: Hello\n[00:02.00][Chorus]\n[00:03.00]<3 you\nNo time here\n[00:04.00]");

        Assert.Equal(["Hello", "[Chorus]", "<3 you", string.Empty], lrc.Lines.Select(l => l.Text));
        Assert.All(lrc.Lines, l => Assert.Empty(l.Words));
    }

    [Fact]
    public void Nothing_to_parse_is_an_empty_document()
    {
        Assert.Empty(LrcParser.Parse(null).Lines);
        Assert.Empty(LrcParser.Parse("  ").Lines);
        Assert.Empty(LrcParser.Parse("just text\nno timestamps").Lines);
    }
}
