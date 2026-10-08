using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class RelatedParserTests
{
    private const string Oasis = "UCmMUZbaYdNH0bEd1PAlAqsA";

    private readonly CapturingLogger _log = new();

    private IReadOnlyList<Shelf> Related() => RelatedParser.Parse(ParserFixtures.Load("related.json"), _log);

    [Fact]
    public void Related_has_the_six_sections_of_get_song_related()
    {
        var shelves = Related();

        Assert.Equal(
            ["You might also like", "Recommended playlists", "Other performances", "Similar artists", "Oasis", "About the artist"],
            shelves.Select(s => s.Title));
        Assert.Equal([20, 10, 7, 9, 33, 0], shelves.Select(s => s.Items.Count));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Song_row_carousels_are_lists_and_the_rest_are_cards()
    {
        var shelves = Related();

        Assert.Equal(
            [ShelfLayout.List, ShelfLayout.Cards, ShelfLayout.List, ShelfLayout.Cards, ShelfLayout.Cards, ShelfLayout.Cards],
            shelves.Select(s => s.Layout));
    }

    [Fact]
    public void You_might_also_like_holds_flat_song_rows()
    {
        var shelf = Related()[0];

        Assert.All(shelf.Items, item => Assert.IsType<Track>(item));
        var song = Assert.IsType<Track>(shelf.Items[0]);
        Assert.Equal(("Wonderwall", "rj5wZqReXQE", TrackType.Song), (song.Title, song.VideoId, song.Type));
        Assert.Equal([new ArtistRef("Oasis", Oasis)], song.Artists);
        Assert.Equal(new AlbumRef("(What's The Story) Morning Glory? (Remastered)", "MPREb_9nqEki4ZDpp"), song.Album);
        Assert.False(song.IsExplicit);
        Assert.NotEmpty(song.Thumbnails);
        Assert.Equal("Fix You", shelf.Items[^1].Title);
    }

    [Fact]
    public void Other_performances_are_songs_by_any_artist()
    {
        var song = Assert.IsType<Track>(Related()[2].Items[0]);

        Assert.Equal(("Wonderwall", "bjoIKgVGiNQ"), (song.Title, song.VideoId));
        Assert.Equal([new ArtistRef("TEEMID", "UCB_tQU5gfHUCSFJ78-N538w")], song.Artists);
        Assert.Equal(new AlbumRef("Wonderwall", "MPREb_cNli1wG8jyp"), song.Album);
    }

    [Fact]
    public void Recommended_playlist_card()
    {
        var shelf = Related()[1];

        Assert.All(shelf.Items, item => Assert.IsType<Playlist>(item));
        var playlist = Assert.IsType<Playlist>(shelf.Items[0]);
        Assert.Equal("Modern Rock Hits", playlist.Title);
        Assert.Equal("RDCLAK5uy_l3PeyHeqJh1dR78WjfsMJwRHJx9ofMvvc", playlist.PlaylistId);
        Assert.Equal("Playlist • YouTube Music", playlist.Description);
        Assert.False(playlist.IsMix);
    }

    [Fact]
    public void Similar_artist_card()
    {
        var shelf = Related()[3];

        Assert.All(shelf.Items, item => Assert.IsType<Artist>(item));
        var artist = Assert.IsType<Artist>(shelf.Items[0]);
        Assert.Equal(("Liam Gallagher", "UCwK2Grm574W1u-sBzLikldQ", "543K"), (artist.Title, artist.BrowseId, artist.Subscribers));
    }

    [Fact]
    public void Artist_albums_shelf_is_titled_with_the_artist_name()
    {
        var shelf = Related()[4];

        Assert.All(shelf.Items, item => Assert.IsType<Album>(item));
        var album = Assert.IsType<Album>(shelf.Items[0]);
        Assert.Equal("(What's The Story) Morning Glory? (30th Anniversary Deluxe Edition)", album.Title);
        Assert.Equal("MPREb_9rUb85NwrKk", album.BrowseId);
        Assert.Equal("OLAK5uy_mbNDS0pZVud699ZqH8T_-O9E5m7_XUSFE", album.AudioPlaylistId);
        Assert.Equal((AlbumType.Album, "2025"), (album.Type, album.Year));
        Assert.Empty(album.Artists); // "Album • 2025": no artist run, as in ytmusicapi

        var ep = Assert.IsType<Album>(shelf.Items[1]);
        Assert.Equal(("Whatever", AlbumType.EP, "2025"), (ep.Title, ep.Type, ep.Year));
    }

    [Fact]
    public void About_the_artist_is_a_text_shelf_with_the_description_as_subtitle()
    {
        var about = Related()[^1];

        Assert.Equal("About the artist", about.Title);
        Assert.Empty(about.Items);
        Assert.NotNull(about.Subtitle);
        Assert.StartsWith("Oasis were a rock band consisting of Liam Gallagher, Paul “Guigsy” Mcguigan", about.Subtitle);
        Assert.EndsWith("calling that project off in late 2014.", about.Subtitle);
    }

    [Fact]
    public void Song_row_without_a_videoId_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("related.json");
        var row = response.At("contents", "sectionListRenderer", "contents", 0, "musicCarouselShelfRenderer", "contents", 0, "musicResponsiveListItemRenderer");
        row.Delete("watchEndpoint", "flexColumns", 0, "musicResponsiveListItemFlexColumnRenderer", "text", "runs", 0, "navigationEndpoint");
        row.AsObject().Remove("overlay");

        var shelves = RelatedParser.Parse(response, _log);

        Assert.Equal(19, shelves[0].Items.Count);
        Assert.Equal("Wonderwall (Unplugged)", shelves[0].Items[0].Title);
        Assert.Contains(_log.Warnings, w => w.Contains("musicResponsiveListItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Response_without_a_section_list_is_empty_with_a_warning()
    {
        Assert.Empty(RelatedParser.Parse(new JsonObject(), _log));
        Assert.NotEmpty(_log.Warnings);
    }
}
