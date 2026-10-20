using HushMusic.Core.Lyrics;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LyricsCleanupTests
{
    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static LyricLine Line(double start, string text, double? end = null) =>
        new(S(start), end is { } e ? S(e) : S(start), text, []);

    [Fact]
    public void Open_ends_run_to_the_next_line_and_the_last_to_the_song_end()
    {
        var lines = LyricsCleanup.Timed([Line(10, "A"), Line(15, "B"), Line(20, "C")], S(30));

        Assert.Equal([S(15), S(20), S(30)], lines.Select(l => l.End));
    }

    [Fact]
    public void Without_the_song_length_the_last_line_lasts_a_few_seconds()
    {
        var last = LyricsCleanup.Timed([Line(10, "A")], null).Single();

        Assert.Equal(S(10) + LyricsCleanup.LastLineFallback, last.End);
    }

    [Fact]
    public void Lines_are_put_in_time_order()
    {
        var lines = LyricsCleanup.Timed([Line(20, "Second"), Line(10, "First")], S(30));

        Assert.Equal(["First", "Second"], lines.Select(l => l.Text));
    }

    [Fact]
    public void An_empty_line_is_a_break_that_ends_the_line_before_it()
    {
        var lines = LyricsCleanup.Timed([Line(10, "A"), Line(14, "  "), Line(20, "♪"), Line(25, "B")], S(30));

        Assert.Equal(2, lines.Count);
        Assert.Equal(S(14), lines[0].End);
        Assert.Equal(S(30), lines[1].End);
    }

    [Fact]
    public void The_sources_own_end_is_kept_but_never_runs_into_the_next_line()
    {
        var lines = LyricsCleanup.Timed([Line(10, "A", 12), Line(15, "B", 19), Line(18, "C", 22)], S(30));

        Assert.Equal([S(12), S(18), S(22)], lines.Select(l => l.End));
    }

    [Fact]
    public void A_caption_repeated_while_still_showing_is_one_line()
    {
        var lines = LyricsCleanup.Timed([Line(10, "Hello", 14), Line(12, "hello", 16), Line(20, "Bye", 21)], S(30), LyricsTextKind.Captions);

        Assert.Equal(2, lines.Count);
        Assert.Equal(S(10), lines[0].Start);
        Assert.Equal(S(16), lines[0].End);
    }

    [Fact]
    public void A_line_sung_twice_in_a_row_stays_twice()
    {
        var lines = LyricsCleanup.Timed([Line(10, "The same test line"), Line(12, "The same test line")], S(30));

        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Lines_starting_together_are_joined()
    {
        var lines = LyricsCleanup.Timed([Line(10, "A"), Line(10.02, "B"), Line(10.04, "B"), Line(15, "C")], S(30));

        Assert.Equal(["A B", "C"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Credit_lines_are_dropped_and_name_the_writers()
    {
        var lrc = LrcParser.Parse("[00:00.00]作词 : 张三\n[00:01.00]作曲 : 李四 / 王五\n[00:01.50]编曲 : Someone Else\n[00:10.00]First line");

        var lines = LyricsCleanup.Timed(lrc.Lines, S(30));

        Assert.Equal(["First line"], lines.Select(l => l.Text));
        Assert.Equal("张三, 李四, 王五", LyricsCleanup.FindWriters(lrc.Lines.Select(l => l.Text)));
        Assert.Equal("A, B", LyricsCleanup.FindWriters(["Written by: A & B", "Produced by: C", "Just a lyric"]));
        Assert.Null(LyricsCleanup.FindWriters(["Hello", "Written by the sea"]));
    }

    [Fact]
    public void Caption_noise_is_removed_and_shouting_calmed()
    {
        var lines = LyricsCleanup.Timed(
            [
                Line(1, "[♪♪♪]", 3),
                Line(4, "♪ I'M THE FIRST TEST LINE ♪", 6),
                Line(7, "(UPBEAT MUSIC)", 9),
                Line(10, "- [MUSIC] AND I SAY A SECOND LINE", 12),
                Line(13, ">> ♫ THE THIRD ONE (OH) ♫", 15),
            ],
            S(30),
            LyricsTextKind.Captions);

        Assert.Equal(["I'm the first test line", "And I say a second line", "The third one (oh)"], lines.Select(l => l.Text));
    }

    [Fact]
    public void Lyrics_in_brackets_stay_unless_they_are_a_section_header()
    {
        Assert.Equal("(Oh, oh)", LyricsCleanup.CleanText("(Oh, oh)", LyricsTextKind.Lyrics));
        Assert.Equal(string.Empty, LyricsCleanup.CleanText("[Chorus: Someone]", LyricsTextKind.Lyrics));
        Assert.Equal("[Hey] you [there]", LyricsCleanup.CleanText("[Hey] you [there]", LyricsTextKind.Lyrics));
    }

    [Fact]
    public void Words_close_at_the_next_word_and_the_last_at_the_line_end()
    {
        var lrc = LrcParser.Parse("[00:10.00]<00:10.00>Hello <00:10.50>world\n[00:12.00]Next");

        var first = LyricsCleanup.Timed(lrc.Lines, S(30))[0];

        Assert.Equal(S(12), first.End);
        Assert.Equal([S(10.5), S(12)], first.Words.Select(w => w.End));
    }

    [Fact]
    public void Plain_text_keeps_single_verse_breaks()
    {
        var lines = LyricsCleanup.Plain("\r\n\r\nVerse one\r\n  line two  \r\n\r\n\r\n[Chorus]\r\nChorus line\r\n♪\r\n\r\nLyricist: Someone\r\n");

        Assert.Equal(["Verse one", "line two", string.Empty, "Chorus line"], lines.Select(l => l.Text));
        Assert.All(lines, l => Assert.Equal(TimeSpan.Zero, l.Start));
        Assert.All(lines, l => Assert.Equal(TimeSpan.Zero, l.End));
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Empty(LyricsCleanup.Plain(null));
        Assert.Empty(LyricsCleanup.Plain(" \n "));
        Assert.Empty(LyricsCleanup.Timed([Line(1, ""), Line(2, "♪")], S(10)));
    }
}
