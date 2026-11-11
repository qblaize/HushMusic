using HushMusic.Core.Lyrics;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LyricsCursorTests
{
    // Made-up timing: an intro break, three lines, a break, a last line.
    private static readonly IReadOnlyList<ITimedLyric> Song =
    [
        Break(0),
        Line(6),
        Line(9),
        Line(12.5),
        Break(15),
        Line(24),
    ];

    [Theory]
    [InlineData(0, 0)]
    [InlineData(5.9, 0)]
    [InlineData(6, 1)]
    [InlineData(10, 2)]
    [InlineData(14.99, 3)]
    [InlineData(15, 4)]
    [InlineData(300, 5)]
    public void The_current_item_is_the_last_one_that_has_started(double seconds, int expected)
    {
        Assert.Equal(expected, LyricsCursor.IndexAt(Song, TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void Before_the_first_line_nothing_is_current()
    {
        IReadOnlyList<ITimedLyric> lines = [Line(2), Line(4)];

        Assert.Equal(-1, LyricsCursor.IndexAt(lines, TimeSpan.FromSeconds(1.5)));
        Assert.Equal(-1, LyricsCursor.IndexAt([], TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public void Lines_starting_together_make_the_last_of_them_current()
    {
        IReadOnlyList<ITimedLyric> lines = [Line(1), Line(3), Line(3), Line(5)];

        Assert.Equal(2, LyricsCursor.IndexAt(lines, TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public void The_next_change_is_when_the_following_item_starts()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), LyricsCursor.UntilNext(Song, 1, TimeSpan.FromSeconds(7)));
        Assert.Equal(TimeSpan.FromSeconds(-0.5), LyricsCursor.UntilNext(Song, 1, TimeSpan.FromSeconds(9.5)));
        Assert.Equal(TimeSpan.FromSeconds(1), LyricsCursor.UntilNext([Line(4)], -1, TimeSpan.FromSeconds(3)));
        Assert.Null(LyricsCursor.UntilNext(Song, 5, TimeSpan.FromSeconds(30)));
        Assert.Null(LyricsCursor.UntilNext([], -1, TimeSpan.Zero));
    }

    [Fact]
    public void A_glance_shows_the_current_line_and_the_next_sung_one()
    {
        Assert.Equal(new LyricsGlance(1, 2, IsPause: false), LyricsCursor.Glance(Song, 1));

        // The break after line 3 is skipped: the dimmed line is the one sung after it.
        Assert.Equal(new LyricsGlance(3, 5, IsPause: false), LyricsCursor.Glance(Song, 3));
    }

    [Fact]
    public void Breaks_and_the_time_before_the_first_line_are_pauses()
    {
        Assert.Equal(new LyricsGlance(0, 1, IsPause: true), LyricsCursor.Glance(Song, 0));
        Assert.Equal(new LyricsGlance(4, 5, IsPause: true), LyricsCursor.Glance(Song, 4));
        Assert.Equal(new LyricsGlance(-1, 0, IsPause: true), LyricsCursor.Glance([Line(3), Line(5)], -1));
    }

    [Fact]
    public void The_last_line_has_nothing_after_it()
    {
        Assert.Equal(new LyricsGlance(5, -1, IsPause: false), LyricsCursor.Glance(Song, 5));
        Assert.Equal(new LyricsGlance(5, -1, IsPause: false), LyricsCursor.Glance(Song, 99));
        Assert.Equal(LyricsGlance.None, LyricsCursor.Glance([], 0));
    }

    [Fact]
    public void Only_a_step_of_one_item_counts_as_the_song_moving_on()
    {
        var intro = LyricsCursor.Glance(Song, 0);
        var first = LyricsCursor.Glance(Song, 1);
        var third = LyricsCursor.Glance(Song, 3);

        Assert.True(first.Follows(intro));
        Assert.True(LyricsCursor.Glance(Song, 4).Follows(third));
        Assert.False(third.Follows(first));
        Assert.False(intro.Follows(first));
        Assert.True(LyricsCursor.Glance([Line(3)], 0).Follows(LyricsCursor.Glance([Line(3)], -1)));

        // The first glance at new lyrics is never a step.
        Assert.False(intro.Follows(LyricsGlance.None));
        Assert.False(LyricsGlance.None.Follows(LyricsGlance.None));
    }

    private static TimedItem Line(double seconds) => new(TimeSpan.FromSeconds(seconds), IsInstrumental: false);

    private static TimedItem Break(double seconds) => new(TimeSpan.FromSeconds(seconds), IsInstrumental: true);

    private sealed record TimedItem(TimeSpan Start, bool IsInstrumental) : ITimedLyric;
}
