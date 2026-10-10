using System.Globalization;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class TrackListViewTests
{
    // A playlist in its own order.
    private static readonly IReadOnlyList<Row> Songs =
    [
        Song("One More Time", "Daft Punk", "Discovery", 320),
        Song("Halo", "Beyoncé", "I Am... Sasha Fierce", 261),
        Song("Hoppípolla", "Sigur Rós", "Takk...", 268),
        Song("around the world", "Daft Punk", "Homework", 429),
        Song("Ágætis byrjun", "Sigur Rós", null, null),
        Song("Breathe", "Télépopmusik", "Genetic World", 279),
    ];

    [Theory]
    [InlineData("Beyoncé", "beyonce")]
    [InlineData("SIGUR RÓS", "sigur ros")]
    [InlineData("Ștefan Bănică", "stefan banica")]
    [InlineData("Straße", "strasse")]
    [InlineData("Mø", "mo")]
    [InlineData("Ægir Łódź", "aegir lodz")]
    [InlineData("Don’t Stop", "don't stop")]
    [InlineData("ＡＢＣ", "abc")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Text_is_folded_to_lower_case_without_accents(string? text, string expected) =>
        Assert.Equal(expected, TrackListView.Normalize(text));

    [Fact]
    public void Default_options_show_every_track_in_order()
    {
        var shown = View().Apply(Songs, TrackListOptions.Default);

        Assert.Equal(Songs, shown);
        Assert.True(TrackListOptions.Default.IsDefault);
    }

    [Fact]
    public void The_filter_matches_title_artists_and_album()
    {
        var view = View();

        Assert.Equal(["Halo"], Titles(view.Apply(Songs, new("halo"))));
        Assert.Equal(["One More Time", "around the world"], Titles(view.Apply(Songs, new("daft"))));
        Assert.Equal(["Breathe"], Titles(view.Apply(Songs, new("genetic"))));
    }

    [Fact]
    public void The_filter_ignores_case_and_accents_both_ways()
    {
        var view = View();

        Assert.Equal(["Halo"], Titles(view.Apply(Songs, new("BEYONCE"))));
        Assert.Equal(["Hoppípolla", "Ágætis byrjun"], Titles(view.Apply(Songs, new("sigur rós"))));
        Assert.Equal(["Ágætis byrjun"], Titles(view.Apply(Songs, new("agaetis"))));
    }

    [Fact]
    public void Every_word_of_the_filter_must_match_somewhere()
    {
        var view = View();

        Assert.Equal(["around the world"], Titles(view.Apply(Songs, new("world  punk"))));
        Assert.Empty(view.Apply(Songs, new("world sigur")));
    }

    [Fact]
    public void Words_never_match_across_fields()
    {
        // "Halo" + "Beyoncé": the end of the title and the start of the artist aren't one word.
        Assert.Empty(View().Apply(Songs, new("lobey")));
    }

    [Fact]
    public void A_blank_filter_is_no_filter()
    {
        var options = new TrackListOptions("   ");

        Assert.False(options.IsFiltered);
        Assert.True(options.IsDefault);
        Assert.Equal(Songs, View().Apply(Songs, options));
    }

    [Fact]
    public void Sorting_by_title_ignores_case_and_accents()
    {
        var shown = View().Apply(Songs, new(Sort: TrackSort.Title));

        Assert.Equal(["Ágætis byrjun", "around the world", "Breathe", "Halo", "Hoppípolla", "One More Time"], Titles(shown));
    }

    [Fact]
    public void A_word_sorts_before_longer_words_it_starts()
    {
        IReadOnlyList<Row> rows = [Song("Ai ochii", "x", null, null), Song("Au aparut", "x", null, null), Song("A venit", "x", null, null)];

        Assert.Equal(["A venit", "Ai ochii", "Au aparut"], Titles(View().Apply(rows, new(Sort: TrackSort.Title))));
    }

    [Fact]
    public void Descending_reverses_the_sort()
    {
        var shown = View().Apply(Songs, new(Sort: TrackSort.Title, Descending: true));

        Assert.Equal(["One More Time", "Hoppípolla", "Halo", "Breathe", "around the world", "Ágætis byrjun"], Titles(shown));
    }

    [Fact]
    public void Ties_keep_the_playlist_order_in_both_directions()
    {
        var view = View();

        var ascending = view.Apply(Songs, new(Sort: TrackSort.Artist));
        var descending = view.Apply(Songs, new(Sort: TrackSort.Artist, Descending: true));

        Assert.Equal(["Halo", "One More Time", "around the world", "Hoppípolla", "Ágætis byrjun", "Breathe"], Titles(ascending));
        Assert.Equal(["Breathe", "Hoppípolla", "Ágætis byrjun", "One More Time", "around the world", "Halo"], Titles(descending));
    }

    [Fact]
    public void Tracks_without_an_album_or_duration_come_last_either_way()
    {
        var view = View();

        Assert.Equal("Ágætis byrjun", Titles(view.Apply(Songs, new(Sort: TrackSort.Album)))[^1]);
        Assert.Equal("Ágætis byrjun", Titles(view.Apply(Songs, new(Sort: TrackSort.Album, Descending: true)))[^1]);
        Assert.Equal("Ágætis byrjun", Titles(view.Apply(Songs, new(Sort: TrackSort.Duration)))[^1]);
        Assert.Equal("Ágætis byrjun", Titles(view.Apply(Songs, new(Sort: TrackSort.Duration, Descending: true)))[^1]);
    }

    [Fact]
    public void Duration_sorts_by_length()
    {
        var view = View();

        Assert.Equal(
            ["Halo", "Hoppípolla", "Breathe", "One More Time", "around the world", "Ágætis byrjun"],
            Titles(view.Apply(Songs, new(Sort: TrackSort.Duration))));
        Assert.Equal(
            ["around the world", "One More Time", "Breathe", "Hoppípolla", "Halo", "Ágætis byrjun"],
            Titles(view.Apply(Songs, new(Sort: TrackSort.Duration, Descending: true))));
    }

    [Fact]
    public void The_custom_order_descending_is_the_playlist_backwards()
    {
        var options = new TrackListOptions(Descending: true);

        Assert.True(options.IsSorted);
        Assert.Equal(Songs.Reverse(), View().Apply(Songs, options));
    }

    [Fact]
    public void Filter_and_sort_combine()
    {
        var shown = View().Apply(Songs, new("daft", TrackSort.Title));

        Assert.Equal(["around the world", "One More Time"], Titles(shown));
    }

    [Fact]
    public void The_same_song_listed_twice_stays_two_rows()
    {
        var song = Songs[1].Track;
        IReadOnlyList<Row> rows = [new(song), Songs[0], new(song)];

        var shown = View().Apply(rows, new("halo", TrackSort.Title));

        Assert.Equal([rows[0], rows[2]], shown);
    }

    [Fact]
    public void Rows_added_later_are_filtered_and_sorted_too()
    {
        var view = View();
        var firstPage = Songs.Take(3).ToList();
        Assert.Equal(["One More Time"], Titles(view.Apply(firstPage, new("daft", TrackSort.Title))));

        var shown = view.Apply(Songs, new("daft", TrackSort.Title));

        Assert.Equal(["around the world", "One More Time"], Titles(shown));
    }

    [Fact]
    public void The_options_say_what_they_change()
    {
        Assert.True(new TrackListOptions("halo").IsFiltered);
        Assert.False(new TrackListOptions("halo").IsSorted);
        Assert.True(new TrackListOptions(Sort: TrackSort.Album).IsSorted);
        Assert.False(new TrackListOptions(Sort: TrackSort.Album).IsDefault);
    }

    [Fact]
    public void Matching_needs_every_term()
    {
        var text = TrackListView.SearchText(Songs[0].Track);

        Assert.Equal("one more time\ndaft punk\ndiscovery", text);
        Assert.True(TrackListView.Matches(text, ["more", "punk"]));
        Assert.False(TrackListView.Matches(text, ["more", "homework"]));
        Assert.True(TrackListView.Matches(text, []));
    }

    private static TrackListView<Row> View() => new(row => row.Track, CultureInfo.InvariantCulture);

    private static List<string> Titles(IEnumerable<Row> rows) => [.. rows.Select(r => r.Track.Title)];

    private static Row Song(string title, string artist, string? album, int? seconds) => new(new Track
    {
        VideoId = title,
        Title = title,
        Artists = [new ArtistRef(artist, null)],
        Album = album is null ? null : new AlbumRef(album, null),
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    });

    // A list row: a class, so two rows with the same song are two rows.
    private sealed class Row(Track track)
    {
        public Track Track { get; } = track;
    }
}
