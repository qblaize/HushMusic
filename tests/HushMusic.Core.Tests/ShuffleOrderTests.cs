using HushMusic.Core.Queue;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class ShuffleOrderTests
{
    private sealed record Song(string Title, string? Artist);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Keeps_every_song_once(int seed)
    {
        var songs = Library(("A", 5), ("B", 3), ("C", 1), (null, 2));

        var order = ShuffleOrder.Spread(songs, s => s.Artist, new Random(seed));

        Assert.Equal(songs.OrderBy(s => s.Title), order.OrderBy(s => s.Title));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Never_plays_the_same_artist_twice_in_a_row_when_it_can_be_avoided(int seed)
    {
        var songs = Library(("A", 6), ("B", 4), ("C", 3), ("D", 1));

        var order = ShuffleOrder.Spread(songs, s => s.Artist, new Random(seed));

        Assert.All(order.Zip(order.Skip(1)), pair => Assert.NotEqual(pair.First.Artist, pair.Second.Artist));
    }

    [Fact]
    public void The_first_song_avoids_the_artist_playing_now()
    {
        var songs = Library(("A", 2), ("B", 2));

        for (var seed = 0; seed < 20; seed++)
        {
            var order = ShuffleOrder.Spread(songs, s => s.Artist, new Random(seed), previousArtist: "A");

            Assert.Equal("B", order[0].Artist);
        }
    }

    [Fact]
    public void Artist_names_match_regardless_of_case_and_spaces()
    {
        Song[] songs = [new("1", "Daft Punk"), new("2", " daft punk "), new("3", "Justice")];

        var order = ShuffleOrder.Spread(songs, s => s.Artist, new Random(7));

        Assert.Equal("Justice", order[1].Artist);
    }

    [Fact]
    public void A_single_artist_is_simply_shuffled()
    {
        var songs = Library(("A", 8));

        var order = ShuffleOrder.Spread(songs, s => s.Artist, new Random(3));

        Assert.Equal(8, order.Count);
        Assert.NotEqual(songs, order);
    }

    [Fact]
    public void An_artist_with_most_of_the_songs_is_spread_over_the_whole_queue()
    {
        var songs = Library(("A", 10), ("B", 10));

        var order = ShuffleOrder.Spread(songs, s => s.Artist, new Random(11));

        Assert.Equal(5, order.Take(10).Count(s => s.Artist == "A"));
    }

    private static List<Song> Library(params (string? Artist, int Count)[] artists) =>
        [.. artists.SelectMany(a => Enumerable.Range(1, a.Count).Select(i => new Song($"{a.Artist ?? "unknown"}-{i}", a.Artist)))];
}
