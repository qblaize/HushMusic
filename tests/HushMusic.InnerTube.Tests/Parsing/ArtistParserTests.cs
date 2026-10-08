using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class ArtistParserTests
{
    private const string DaftPunkId = "UCRr1xG_2WIDs18a6cIiCxeA";

    private readonly CapturingLogger _log = new();

    [Fact]
    public void Artist_header()
    {
        var page = ArtistParser.Parse(ParserFixtures.Load("artist.json"), DaftPunkId, _log);

        Assert.Equal("Daft Punk", page.Artist.Title);
        Assert.Equal(DaftPunkId, page.Artist.BrowseId); // the subscribe button's channelId (UC_kRDKY...) differs
        Assert.Equal("7.19M", page.Artist.Subscribers);
        Assert.NotEmpty(page.Artist.Thumbnails);
        Assert.Equal("6,030,217,324 views", page.Views);
        Assert.False(string.IsNullOrWhiteSpace(page.Description));
        Assert.Equal("RDAOni3jl65KF37F9JYsJM8DGg", page.ShufflePlaylistId);
        Assert.Equal("RDEMni3jl65KF37F9JYsJM8DGg", page.RadioPlaylistId);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Artist_top_songs()
    {
        var page = ArtistParser.Parse(ParserFixtures.Load("artist.json"), DaftPunkId, _log);

        Assert.Equal("OLAK5uy_lNVBcjNtiCwq-n95uoxJ-Gd4vsfMkyZNs", page.AllSongsPlaylistId);
        Assert.Equal(5, page.TopSongs.Count);
        var first = page.TopSongs[0];
        Assert.Equal(("Instant Crush (feat. Julian Casablancas)", "khnokW3Mw24"), (first.Title, first.VideoId));
        Assert.Equal([new ArtistRef("Daft Punk", DaftPunkId), new ArtistRef("Julian Casablancas", "UCWhpbdGLnR8XtXLr0yl2nYA")], first.Artists);
        Assert.Equal(new AlbumRef("Random Access Memories", "MPREb_K8qWMWVqXGi"), first.Album);
    }

    [Fact]
    public void Every_carousel_becomes_a_section_in_page_order()
    {
        var page = ArtistParser.Parse(ParserFixtures.Load("artist.json"), DaftPunkId, _log);

        // ytmusicapi drops "Live performances", "Featured on" and "Playlists by Daft Punk" (unknown English titles).
        Assert.Equal(
            ["Albums", "Singles & EPs", "Videos", "Live performances", "Featured on", "Playlists by Daft Punk", "Fans might also like"],
            page.Sections.Select(s => s.Title));
        Assert.All(page.Sections, s => Assert.Equal(10, s.Items.Count));
    }

    [Theory]
    [InlineData("artist.json", DaftPunkId)]
    [InlineData("artist_small.json", "UCLZ7tlKC06ResyDmEStSrOw")]
    public void Artist_carousels_are_cards(string fixture, string channelId)
    {
        // "Top songs" is a musicShelfRenderer list (TopSongs), not a carousel; every carousel holds cards.
        var page = ArtistParser.Parse(ParserFixtures.Load(fixture), channelId, _log);

        Assert.NotEmpty(page.Sections);
        Assert.All(page.Sections, s => Assert.Equal(ShelfLayout.Cards, s.Layout));
    }

    [Fact]
    public void Artist_albums_singles_and_more_links()
    {
        var sections = ArtistParser.Parse(ParserFixtures.Load("artist.json"), DaftPunkId, _log).Sections;

        var albums = sections[0];
        Assert.Null(albums.MoreBrowseId); // title has no link
        var album = Assert.IsType<Album>(albums.Items[0]);
        Assert.Equal(("Random Access Memories (Drumless Edition)", "MPREb_OgeEnoHTsCm", "2023"), (album.Title, album.BrowseId, album.Year));
        Assert.All(albums.Items, i => Assert.IsType<Album>(i));

        var singles = sections[1];
        Assert.Equal("MPADUCRr1xG_2WIDs18a6cIiCxeA", singles.MoreBrowseId);
        Assert.Equal("ggMIegYIAhoCAQI%3D", singles.MoreParams);
        var single = Assert.IsType<Album>(singles.Items[0]);
        Assert.Equal(("GLBTM (Studio Outtakes)", AlbumType.Single, "2023"), (single.Title, single.Type, single.Year));
    }

    [Fact]
    public void Artist_videos_playlists_and_related_artists()
    {
        var sections = ArtistParser.Parse(ParserFixtures.Load("artist.json"), DaftPunkId, _log).Sections;

        var videos = sections[2];
        Assert.Equal("VLOLAK5uy_mvGpNqcAglCR8WbCLLHnIitRBdLjho0ZI", videos.MoreBrowseId);
        var video = Assert.IsType<Track>(videos.Items[0]);
        Assert.Equal(("Instant Crush (feat. Julian Casablancas)", "a5uQMwRMHcs"), (video.Title, video.VideoId));
        Assert.Equal("901M views", video.Views); // ytmusicapi keeps "901M"
        Assert.Equal([new ArtistRef("Daft Punk", DaftPunkId)], video.Artists);

        Assert.All(sections[4].Items, i => Assert.IsType<Playlist>(i)); // Featured on
        var byArtist = Assert.IsType<Playlist>(sections[5].Items[0]); // Playlists by Daft Punk
        Assert.Equal("PLSdoVPM5WnndSQEXRz704yQkKwx76GvPV", byArtist.PlaylistId);
        Assert.Equal("Daft Punk", byArtist.Author?.Name);

        var related = Assert.IsType<Artist>(sections[6].Items[0]);
        Assert.Equal(("Gorillaz", "UCNIV5B_aJnLrKDSnW_MOmcQ"), (related.Title, related.BrowseId));
        Assert.Equal("75.9M", related.Subscribers); // the subtitle is now "75.9M monthly audience"
    }

    [Fact]
    public void Small_artist_page()
    {
        var page = ArtistParser.Parse(ParserFixtures.Load("artist_small.json"), "UCLZ7tlKC06ResyDmEStSrOw", _log);

        Assert.Equal("Дружки", page.Artist.Title);
        Assert.Equal("381", page.Artist.Subscribers);
        Assert.Null(page.Description);
        Assert.Null(page.Views);
        Assert.Equal("OLAK5uy_m3TbjGwBH-BrYM-lmEOdzy4XLFqwAJgd4", page.AllSongsPlaylistId);
        Assert.Equal(5, page.TopSongs.Count);
        Assert.Null(page.TopSongs[0].Views);
        Assert.Equal(["Albums", "Singles & EPs", "Videos"], page.Sections.Select(s => s.Title));

        var albums = page.Sections[0];
        Assert.Equal("MPADUCLZ7tlKC06ResyDmEStSrOw", albums.MoreBrowseId);
        Assert.Equal("ggMIegYIARoCAQI%3D", albums.MoreParams);
        var album = Assert.IsType<Album>(albums.Items[0]);
        Assert.Equal(("Герасим & Высочин: Песни оттуда", "2020", AlbumType.Album), (album.Title, album.Year, album.Type));

        var singles = page.Sections[1];
        Assert.Null(singles.MoreBrowseId);
        var ep = Assert.IsType<Album>(Assert.Single(singles.Items));
        Assert.Equal(("Gerasim & Kuchumov: Yellow Blue Bus", "MPREb_IuYaJp7imG5", AlbumType.EP, "2018"), (ep.Title, ep.BrowseId, ep.Type, ep.Year));
    }

    [Fact]
    public void MPLA_prefix_is_stripped_from_the_browse_id()
    {
        var page = ArtistParser.Parse(ParserFixtures.Load("artist.json"), "MPLA" + DaftPunkId, _log);
        Assert.Equal(DaftPunkId, page.Artist.BrowseId);
    }

    [Fact]
    public void Discography_grid()
    {
        var page = ArtistParser.ParseDiscography(ParserFixtures.Load("artist_albums.json"), _log);

        Assert.Equal(27, page.Items.Count);
        Assert.Null(page.Continuation);
        Assert.Equal(24, page.Items.Count(a => a.Type == AlbumType.Single));
        Assert.Equal(3, page.Items.Count(a => a.Type == AlbumType.EP));
        Assert.All(page.Items, a => Assert.Empty(a.Artists));

        var first = page.Items[0];
        Assert.Equal(("MPREb_X1DQ1j0PPrX", "OLAK5uy_k29fGfk85tfMcdDEJPdinu2E9VgdHuCIU", "GLBTM (Studio Outtakes)", "2023"),
            (first.BrowseId, first.AudioPlaylistId, first.Title, first.Year));
        var last = page.Items[^1];
        Assert.Equal(("MPREb_TP971moS91e", "OLAK5uy_mDMbRmjn5kvZIutsNwYHLHSz9MxuTRO-s", "Human After All (Remixes)", AlbumType.EP, "2005"),
            (last.BrowseId, last.AudioPlaylistId, last.Title, last.Type, last.Year));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Album_card_without_title_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("artist.json");
        response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 1,
            "musicCarouselShelfRenderer", "contents", 0, "musicTwoRowItemRenderer").AsObject().Remove("title");

        var page = ArtistParser.Parse(response, DaftPunkId, _log);

        Assert.Equal(9, page.Sections[0].Items.Count);
        Assert.Equal("Random Access Memories", page.Sections[0].Items[0].Title);
        Assert.Equal(7, page.Sections.Count);
        Assert.Contains(_log.Warnings, w => w.Contains("musicTwoRowItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_an_empty_page()
    {
        var page = ArtistParser.Parse(new JsonObject(), DaftPunkId, _log);
        Assert.Equal(DaftPunkId, page.Artist.BrowseId);
        Assert.Empty(page.Sections);
        Assert.Empty(page.TopSongs);
        Assert.Empty(ArtistParser.ParseDiscography(new JsonObject(), _log).Items);
        Assert.NotEmpty(_log.Warnings);
    }
}
