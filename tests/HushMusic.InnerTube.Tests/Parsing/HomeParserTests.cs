using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using HushMusic.InnerTube.Parsing.Common;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class HomeParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Home_first_page_has_two_shelves_and_skips_the_tastebuilder()
    {
        var page = HomeParser.Parse(ParserFixtures.Load("home.json"), _log);

        Assert.Equal(["The sound of autumn", "Morning boost"], page.Items.Select(s => s.Title));
        Assert.All(page.Items, s => Assert.Equal(10, s.Items.Count));
        Assert.NotNull(page.Items[0].Subtitle); // the header also has a strapline
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Home_playlist_cards_strip_the_VL_prefix_and_keep_the_subtitle_as_description()
    {
        var page = HomeParser.Parse(ParserFixtures.Load("home.json"), _log);

        var first = Assert.IsType<Playlist>(page.Items[0].Items[0]);
        Assert.Equal("Classical for Autumn", first.Title);
        Assert.Equal("RDCLAK5uy_nxpDij9qedpxoq-Fuzu_EC1PcFNB3txpY", first.PlaylistId);
        Assert.Equal("Johann Sebastian Bach, Claude Debussy, Antonio Vivaldi", first.Description);
        Assert.NotEmpty(first.Thumbnails);

        var second = Assert.IsType<Playlist>(page.Items[1].Items[0]);
        Assert.Equal("Feel-Good Pop & Rock", second.Title);
        Assert.Equal("RDCLAK5uy_m0wlRoNn5iCTTgBedfoOQ19Jq9P3XTLIA", second.PlaylistId);
        Assert.Equal("Ed Sheeran, Imagine Dragons, 5 Seconds of Summer, Twenty One Pilots", second.Description);
    }

    [Fact]
    public void Home_first_page_returns_the_raw_section_list_token()
    {
        var response = ParserFixtures.Load("home.json");
        var raw = response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer",
            "continuations", 0, "nextContinuationData", "continuation").GetValue<string>();

        var page = HomeParser.Parse(response, _log);

        Assert.Equal(raw, page.Continuation);
        Assert.Equal(398, page.Continuation!.Length);
    }

    [Fact]
    public void Home_continuation_has_new_releases_quick_picks_and_featured_playlists()
    {
        var page = HomeParser.ParseContinuation(ParserFixtures.Load("home_continuation.json"), _log);

        Assert.Equal(["New releases", "Quick picks", "Featured playlists for you"], page.Items.Select(s => s.Title));
        Assert.Equal([10, 9, 5], page.Items.Select(s => s.Items.Count));
        Assert.Null(page.Continuation); // end of the anonymous feed
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Home_continuation_album_card()
    {
        var page = HomeParser.ParseContinuation(ParserFixtures.Load("home_continuation.json"), _log);

        var album = Assert.IsType<Album>(page.Items[0].Items[0]);
        Assert.Equal("Baiat de calitate", album.Title);
        Assert.Equal("MPREb_5wKw5tSD0b4", album.BrowseId);
        Assert.Equal("OLAK5uy_nso38RBAF9tirX0yMD7gWbA8ROpAb4bUM", album.AudioPlaylistId);
        Assert.Equal(AlbumType.Single, album.Type);
        Assert.Equal([new ArtistRef("Nicolae Guta", "UCAkTuOHJleyE4lk-n_taf9A")], album.Artists);
        Assert.False(album.IsExplicit);
    }

    [Fact]
    public void Home_continuation_quick_picks_are_flat_song_rows()
    {
        var page = HomeParser.ParseContinuation(ParserFixtures.Load("home_continuation.json"), _log);

        var song = Assert.IsType<Track>(page.Items[1].Items[0]);
        Assert.Equal("Patient Zero", song.Title);
        Assert.Equal("V-uIp-WuD60", song.VideoId);
        Assert.Equal(TrackType.Song, song.Type); // MUSIC_VIDEO_TYPE_ATV
        Assert.Equal([new ArtistRef("Taylor Swift", "UCPC0L1d253x-KuMNwa05TpA")], song.Artists);
        Assert.Equal(new AlbumRef("The Life of a Showgirl: The Encore", "MPREb_L64BnYDQpHZ"), song.Album);
        Assert.Equal("19M plays", song.Views); // ytmusicapi keeps only "19M"
        Assert.All(page.Items[1].Items, item => Assert.IsType<Track>(item));
    }

    [Fact]
    public void Quick_picks_is_the_only_list_shelf()
    {
        var first = HomeParser.Parse(ParserFixtures.Load("home.json"), _log);
        var next = HomeParser.ParseContinuation(ParserFixtures.Load("home_continuation.json"), _log);

        Assert.All(first.Items, s => Assert.Equal(ShelfLayout.Cards, s.Layout));
        Assert.Equal([ShelfLayout.Cards, ShelfLayout.List, ShelfLayout.Cards], next.Items.Select(s => s.Layout));
    }

    [Fact]
    public void Layout_follows_the_first_item_renderer()
    {
        Assert.Equal(ShelfLayout.Cards, ShelfParser.LayoutOf(null));
        Assert.Equal(ShelfLayout.Cards, ShelfParser.LayoutOf([]));
        Assert.Equal(ShelfLayout.List, ShelfParser.LayoutOf([new JsonObject { ["musicResponsiveListItemRenderer"] = new JsonObject() }]));
        Assert.Equal(ShelfLayout.Cards, ShelfParser.LayoutOf([new JsonObject { ["musicTwoRowItemRenderer"] = new JsonObject() }]));
        Assert.Equal(ShelfLayout.Cards, ShelfParser.LayoutOf([new JsonObject { ["musicMultiRowListItemRenderer"] = new JsonObject() }]));
    }

    [Fact]
    public void Home_continuation_featured_playlist()
    {
        var page = HomeParser.ParseContinuation(ParserFixtures.Load("home_continuation.json"), _log);

        var playlist = Assert.IsType<Playlist>(page.Items[2].Items[0]);
        Assert.Equal("Trending 20 Romania", playlist.Title);
        Assert.Equal("OLAK5uy_lcrATJDe23r1ypBltst3R6P7UTJSPdNas", playlist.PlaylistId);
        Assert.Equal("Chart • YouTube Charts", playlist.Description);
    }

    [Fact]
    public void Card_without_any_browseId_is_skipped_with_a_warning_and_the_rest_parses()
    {
        var response = ParserFixtures.Load("home.json");
        var card = response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer",
            "contents", 0, "musicCarouselShelfRenderer", "contents", 0, "musicTwoRowItemRenderer");
        card.Delete("browseId", "title", "runs", 0, "navigationEndpoint", "browseEndpoint");
        card.Delete("browseId", "navigationEndpoint", "browseEndpoint");

        var page = HomeParser.Parse(response, _log);

        Assert.Equal(9, page.Items[0].Items.Count);
        Assert.Equal("Chill House", page.Items[0].Items[0].Title);
        Assert.Equal(10, page.Items[1].Items.Count);
        Assert.Contains(_log.Warnings, w => w.Contains("musicTwoRowItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Album_card_without_title_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("home_continuation.json");
        response.At("continuationContents", "sectionListContinuation", "contents", 0, "musicCarouselShelfRenderer", "contents", 0, "musicTwoRowItemRenderer")
            .AsObject().Remove("title");

        var page = HomeParser.ParseContinuation(response, _log);

        Assert.Equal(9, page.Items[0].Items.Count);
        Assert.Contains(_log.Warnings, w => w.Contains("album card", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_an_empty_page()
    {
        Assert.Empty(HomeParser.Parse(new JsonObject(), _log).Items);
        Assert.Empty(HomeParser.ParseContinuation(new JsonObject(), _log).Items);
        Assert.NotEmpty(_log.Warnings);
    }

    [Fact]
    public void Null_response_throws()
    {
        Assert.Throws<ArgumentNullException>(() => HomeParser.Parse(null!, _log));
    }
}
