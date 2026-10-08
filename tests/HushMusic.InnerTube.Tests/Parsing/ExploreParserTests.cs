using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using HushMusic.InnerTube.Parsing.Common;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class ExploreParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Explore_new_releases()
    {
        var page = ExploreParser.Parse(ParserFixtures.Load("explore.json"), _log);

        Assert.Equal(24, page.NewReleases.Count);
        var first = page.NewReleases[0];
        Assert.Equal(("Baiat de calitate", "MPREb_5wKw5tSD0b4", "OLAK5uy_nso38RBAF9tirX0yMD7gWbA8ROpAb4bUM"), (first.Title, first.BrowseId, first.AudioPlaylistId));
        Assert.Equal(AlbumType.Single, first.Type);
        Assert.Null(first.Year);
        Assert.Equal([new ArtistRef("Nicolae Guta", "UCAkTuOHJleyE4lk-n_taf9A")], first.Artists);
        Assert.Equal(("Copilărie plecată", "MPREb_WKzMzIXY2Bb"), (page.NewReleases[^1].Title, page.NewReleases[^1].BrowseId));
        Assert.Equal(17, page.NewReleases.Count(a => a.Type == AlbumType.Single));
        Assert.Equal(3, page.NewReleases.Count(a => a.Type == AlbumType.Album));
        Assert.Equal(4, page.NewReleases.Count(a => a.Type == AlbumType.EP));
        Assert.Equal(2, page.NewReleases.Count(a => a.IsExplicit));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Explore_trending_is_a_ranked_chart()
    {
        var page = ExploreParser.Parse(ParserFixtures.Load("explore.json"), _log);

        // Signed out there is no "Top songs" chart (ytmusicapi: Premium accounts only).
        Assert.Null(page.TopSongs);
        var trending = Assert.IsType<SongChart>(page.Trending);
        Assert.Equal("Trending", trending.Title);
        Assert.Equal("OLAK5uy_lcrATJDe23r1ypBltst3R6P7UTJSPdNas", trending.PlaylistId);
        Assert.Equal(20, trending.Entries.Count);
        Assert.Equal(Enumerable.Range(1, 20), trending.Entries.Select(e => e.Rank ?? 0));
        Assert.All(trending.Entries, e => Assert.Null(e.Trend));

        var first = trending.Entries[0].Item;
        Assert.Equal(("Mts930Jx-3M", TrackType.Video), (first.VideoId, first.Type));
        Assert.Equal("Luis Gabriel & @Haziran - Inimă fără noroc ❤️‍🩹 Official Video", first.Title);
        Assert.Equal("OLAK5uy_lcrATJDe23r1ypBltst3R6P7UTJSPdNas", first.PlaylistId);
        Assert.Equal([new ArtistRef("Luis Gabriel", "UCW_mfs0btK6pzgvo9XBlunA")], first.Artists);
        Assert.Equal("4.4M views", first.Views); // ytmusicapi keeps "4.4M"
        Assert.Null(first.Album);

        // MUSIC_VIDEO_TYPE: 16 OMV + 2 UGC videos, 2 ATV songs.
        Assert.Equal(2, trending.Entries.Count(e => e.Item.Type == TrackType.Song));
        Assert.Equal(18, trending.Entries.Count(e => e.Item.Type == TrackType.Video));
    }

    [Fact]
    public void Explore_moods_and_new_videos()
    {
        var page = ExploreParser.Parse(ParserFixtures.Load("explore.json"), _log);

        Assert.Equal(36, page.MoodsAndGenres.Count);
        Assert.Equal(new MoodCategory("Chill", "ggMPOg1uX1JOQWZFeDByc2Jm") { Color = 0xFFA4C5FF }, page.MoodsAndGenres[0]);
        Assert.Equal(("Soundtracks & musicals", "ggMPOg1uX2tWZXBsRm05cHNR"), (page.MoodsAndGenres[^1].Title, page.MoodsAndGenres[^1].Params));
        Assert.All(page.MoodsAndGenres, m => Assert.NotNull(m.Color));

        Assert.Equal(24, page.NewVideos.Count);
        var video = page.NewVideos[0];
        Assert.Equal(("No Era Para Mí | FireVolk", "0lXbg9H42jc"), (video.Title, video.VideoId));
        Assert.Equal([new ArtistRef("FireVolk", "UCCTAmJyy3lwopWTqkZGNXXw")], video.Artists);
        Assert.Equal("23K views", video.Views);
        Assert.Null(video.PlaylistId);
        Assert.Equal(("Vulnerability (Therapy Session)", "1dG0ZrccYSI"), (page.NewVideos[^1].Title, page.NewVideos[^1].VideoId));
    }

    [Fact]
    public void Explore_skips_sections_it_does_not_know()
    {
        var response = ParserFixtures.Load("explore.json");
        var sections = response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents").AsArray();
        sections[1]!.At("musicCarouselShelfRenderer", "header", "musicCarouselShelfBasicHeaderRenderer", "title", "runs", 0, "navigationEndpoint", "browseEndpoint")["browseId"] = "FEmusic_something_new";

        var page = ExploreParser.Parse(response, _log);

        Assert.Empty(page.NewReleases);
        Assert.NotEmpty(page.MoodsAndGenres);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Explore_without_sections_is_empty_and_logged()
    {
        var page = ExploreParser.Parse(System.Text.Json.Nodes.JsonNode.Parse("""{"contents":{}}""")!, _log);

        Assert.Empty(page.NewReleases);
        Assert.Null(page.Trending);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void Mood_categories_are_grouped()
    {
        var groups = ExploreParser.ParseMoodCategories(ParserFixtures.Load("mood_categories.json"), _log);

        Assert.Equal(["Moods & moments", "Genres"], groups.Select(g => g.Title));
        Assert.Equal([12, 24], groups.Select(g => g.Categories.Count));
        Assert.Equal(new MoodCategory("Chill", "ggMPOg1uX1JOQWZFeDByc2Jm") { Color = 0xFFA4C5FF }, groups[0].Categories[0]);
        Assert.Equal(("Gaming", "ggMPOg1uX3NmUVV4Vzl3WGQ0"), (groups[0].Categories[6].Title, groups[0].Categories[6].Params));
        Assert.Equal(("Soundtracks & musicals", "ggMPOg1uX2tWZXBsRm05cHNR"), (groups[1].Categories[^1].Title, groups[1].Categories[^1].Params));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Mood_category_button_without_params_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("mood_categories.json");
        response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "gridRenderer", "items", 0)
            .Delete("clickCommand", "musicNavigationButtonRenderer");

        var groups = ExploreParser.ParseMoodCategories(response, _log);

        Assert.Equal(11, groups[0].Categories.Count);
        Assert.Single(_log.Warnings);
    }

    [Fact]
    public void Mood_page_keeps_its_carousels_and_their_sub_categories()
    {
        var page = ExploreParser.ParseMoodPlaylists(ParserFixtures.Load("mood_playlists.json"), _log);

        Assert.Equal("Gaming", page.Title);
        Assert.Equal(["Popular gaming playlists", "Gaming moods", "Gaming soundtracks"], page.Sections.Select(s => s.Title));
        Assert.Equal(27, page.Sections.Sum(s => s.Items.Count)); // ytmusicapi: 27 playlists
        Assert.All(page.Sections.SelectMany(s => s.Items), i => Assert.IsType<Playlist>(i));
        Assert.All(page.Sections, s => Assert.Equal("FEmusic_moods_and_genres_category", s.MoreBrowseId));
        Assert.Equal("ggMPOg1uX0p6MXdTVWFSTDF2", page.Sections[0].MoreParams);

        var first = Assert.IsType<Playlist>(page.Sections[0].Items[0]);
        Assert.Equal(("Gaming Hits", "RDCLAK5uy_n9PTezEY6LrODsz6rl2KDtFzC9e5Qzz9Y", "Playlist • YouTube Music"), (first.Title, first.PlaylistId, first.Description));
        var last = Assert.IsType<Playlist>(page.Sections[^1].Items[^1]);
        Assert.Equal(("Chrono Series", "RDCLAK5uy_nShleEbyK-5JU7DmyatrGQZMpvEDT6kEA"), (last.Title, last.PlaylistId));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Genre_page_parses_every_card_type()
    {
        // ytmusicapi get_mood_playlists raises a KeyError on this page (the video cards have no browse link).
        var page = ExploreParser.ParseMoodPlaylists(ParserFixtures.Load("genre_playlists.json"), _log);

        Assert.Equal("Blues", page.Title);
        Assert.Equal(["Songs", "Featured playlists", "Community playlists", "Music videos", "Albums"], page.Sections.Select(s => s.Title));
        Assert.All(page.Sections, s => Assert.Equal(6, s.Items.Count));
        Assert.All(page.Sections, s => Assert.Null(s.MoreBrowseId));

        var songs = page.Sections[0];
        Assert.Equal(ShelfLayout.List, songs.Layout);
        var song = Assert.IsType<Track>(songs.Items[0]);
        Assert.Equal(("Am Facut De Toate In Viata", "TJ8cLT7HRaw"), (song.Title, song.VideoId));
        Assert.Equal("Octavian Nelutu", song.Artists[0].Name);

        var featured = Assert.IsType<Playlist>(page.Sections[1].Items[0]);
        Assert.Equal(("Blues Instrumentals", "RDCLAK5uy_m-qCrzwr92eguM1Hqshp9oBI1Uf4u7SxE"), (featured.Title, featured.PlaylistId));
        var community = Assert.IsType<Playlist>(page.Sections[2].Items[0]);
        Assert.Equal(("Heavy Blues", "PLcqbs9CFq_XR8oTqZeBF4hycFv5Fbr65t"), (community.Title, community.PlaylistId));

        var video = Assert.IsType<Track>(page.Sections[3].Items[0]);
        Assert.Equal("KB_VN04LAvA", video.VideoId);
        Assert.Equal("3.1M views", video.Views);

        // ytmusicapi's parse_playlist turns this into the playlist id "REb_8O9DKJkYQOs".
        var album = Assert.IsType<Album>(page.Sections[4].Items[0]);
        Assert.Equal(("Blondu De La Timisoara Best Of", "MPREb_8O9DKJkYQOs", AlbumType.Album), (album.Title, album.BrowseId, album.Type));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void New_releases_grid()
    {
        var albums = ExploreParser.ParseNewReleases(ParserFixtures.Load("new_releases_albums.json"), _log);

        Assert.Equal(12, albums.Count); // fixture trimmed from 95
        Assert.Equal(("BAIXO SONICO", "MPREb_s0eoZK0Jo7o", AlbumType.EP), (albums[0].Title, albums[0].BrowseId, albums[0].Type));
        Assert.Equal("OLAK5uy_movCFF1W7iA0Sj3-si3Q9tCuCCU9x8K2E", albums[0].AudioPlaylistId);
        Assert.Equal(
            [new ArtistRef("SEKIMANE", "UCiPh9nI38w1389qtuOG76SA"), new ArtistRef("ZMAJOR", "UC2BbaDDLKosyVhEvKsqwwZA"), new ArtistRef("Prey", "UC8sYELn38asKLH68WXWDuTg")],
            albums[0].Artists);
        Assert.Equal(("Umbrella", "MPREb_F37zRNywFdE"), (albums[^1].Title, albums[^1].BrowseId));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Video_filed_as_an_episode_plays_by_its_video_id()
    {
        // Its title links to an MPED... episode page (MUSIC_PAGE_TYPE_NON_MUSIC_AUDIO_TRACK_PAGE); the videoId is in the menu.
        var card = ParserFixtures.Load("new_releases_videos.json")
            .At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0, "gridRenderer", "items", 7, "musicTwoRowItemRenderer")
            .AsObject();

        var track = Assert.IsType<Track>(TwoRowItemParser.Parse(card, new ParseScope(_log, "Test")));

        Assert.Equal(("Vaelissed - Never look back (Official Music Video)", "YfjXn0Q2PhY", TrackType.Episode), (track.Title, track.VideoId, track.Type));
        Assert.Equal([new ArtistRef("Vaelissed", "UCnSaV5p9fstSONyLFzy04pg")], track.Artists);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void New_videos_grid()
    {
        var videos = ExploreParser.ParseNewVideos(ParserFixtures.Load("new_releases_videos.json"), _log);

        Assert.Equal(12, videos.Count); // fixture trimmed from 100
        Assert.Equal(("No Era Para Mí | FireVolk", "0lXbg9H42jc", TrackType.Video), (videos[0].Title, videos[0].VideoId, videos[0].Type));
        Assert.Equal(("Estoy Mejor Sin Ti", "kinvmwNfAt0"), (videos[^1].Title, videos[^1].VideoId));
        Assert.Equal([new ArtistRef("FireVolk", "UCweU4ua1zvliFuBO9gdPnbw")], videos[^1].Artists);
        Assert.Empty(_log.Warnings);
    }
}
