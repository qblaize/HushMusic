using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class SearchParserTests
{
    private const string DaftPunkId = "UCRr1xG_2WIDs18a6cIiCxeA";

    private readonly CapturingLogger _log = new();

    [Fact]
    public void Unfiltered_top_result_is_the_artist_card()
    {
        var results = SearchParser.Parse(ParserFixtures.Load("search_all.json"), SearchFilter.All, _log);

        var artist = Assert.IsType<Artist>(results.TopResult);
        Assert.Equal("Daft Punk", artist.Title);
        Assert.Equal(DaftPunkId, artist.BrowseId);
        Assert.Equal("79.6M", artist.Subscribers);
        Assert.NotEmpty(artist.Thumbnails);
    }

    [Fact]
    public void Unfiltered_flat_layout_becomes_one_untitled_shelf_with_every_kind()
    {
        var results = SearchParser.Parse(ParserFixtures.Load("search_all.json"), SearchFilter.All, _log);

        var shelf = Assert.Single(results.Sections);
        Assert.Equal(string.Empty, shelf.Title);
        Assert.Null(results.Continuation);

        // ytmusicapi's 33 results minus the top result and its 3 podcasts (no podcast model).
        Assert.Equal(29, shelf.Items.Count);
        Assert.Equal(6, shelf.Items.OfType<Artist>().Count()); // 3 artists + 3 profiles
        Assert.Equal(3, shelf.Items.OfType<Album>().Count());
        Assert.Equal(6, shelf.Items.OfType<Playlist>().Count());
        var tracks = shelf.Items.OfType<Track>().ToList();
        Assert.Equal(8, tracks.Count(t => t.Type == TrackType.Song));
        Assert.Equal(3, tracks.Count(t => t.Type == TrackType.Video));
        Assert.Equal(3, tracks.Count(t => t.Type == TrackType.Episode));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Unfiltered_top_result_card_songs_come_first()
    {
        var results = SearchParser.Parse(ParserFixtures.Load("search_all.json"), SearchFilter.All, _log);

        var cardSongs = results.Sections[0].Items.Take(3).Cast<Track>().ToList();
        Assert.Equal(["Face to Face", "Aerodynamic", "Touch (feat. Paul Williams)"], cardSongs.Select(t => t.Title));
        Assert.Equal(["qXI87eMP-bs", "52fNscjkST4", "RRMbhEdmhYw"], cardSongs.Select(t => t.VideoId));
        Assert.Equal([241, 213, 499], cardSongs.Select(t => (int)t.Duration!.Value.TotalSeconds));
        Assert.Equal(["56M plays", "76M plays", "35M plays"], cardSongs.Select(t => t.Views));
        Assert.All(cardSongs, t => Assert.Empty(t.Artists)); // "Song • 4:01" has no artist
    }

    [Fact]
    public void Unfiltered_song_album_and_profile_rows()
    {
        var items = SearchParser.Parse(ParserFixtures.Load("search_all.json"), SearchFilter.All, _log).Sections[0].Items;

        var voyager = items.OfType<Track>().First(t => t.Type == TrackType.Song && t.Duration is null);
        Assert.Equal("Voyager", voyager.Title);
        Assert.Equal("OWiVJMgms9E", voyager.VideoId);
        Assert.Equal([new ArtistRef("Daft Punk", DaftPunkId)], voyager.Artists);
        Assert.Equal("52M plays", voyager.Views);

        var album = items.OfType<Album>().First();
        Assert.Equal("Human After All (Medley)", album.Title);
        Assert.Equal(AlbumType.Single, album.Type);
        Assert.Equal("2005", album.Year);
        Assert.Equal("MPREb_0TDFwaZe4b2", album.BrowseId);
        Assert.Equal("OLAK5uy_nIdq0JtBX7X--kFx8nJ262FPCoyabpm5g", album.AudioPlaylistId);
        Assert.Equal([new ArtistRef("Daft Punk", DaftPunkId)], album.Artists);

        // Profiles are reported as artists (browseId prefix UC), as in ytmusicapi.
        var artists = items.OfType<Artist>().ToList();
        Assert.Contains(artists, a => a is { Title: "un weon fan de Daft Punk ", BrowseId: "UCOfqxviw4fw5DoNkWJDZAlw" });
        Assert.Contains(artists, a => a is { Title: "Retrobeatzz", BrowseId: "UCcRb3nvdoEUj7NLSPn0CNYw" });
    }

    [Fact]
    public void Unfiltered_playlist_rows_read_the_song_count_after_the_author()
    {
        var playlists = SearchParser.Parse(ParserFixtures.Load("search_all.json"), SearchFilter.All, _log)
            .Sections[0].Items.OfType<Playlist>().ToList();

        // "Playlist • YouTube Music • 35 songs": ytmusicapi reads the author run as the count and returns None.
        var presenting = playlists.Single(p => p.Title == "Presenting Daft Punk");
        Assert.Equal(35, presenting.TrackCount);
        Assert.Equal(new ArtistRef("YouTube Music", null), presenting.Author);
        Assert.Equal("RDCLAK5uy_n20FRYQXNt1p1wS55Nj2r14IouO5weaYU", presenting.PlaylistId);

        var greatestHits = playlists.Single(p => p.Title == "Daft Punk Greatest Hits");
        Assert.Null(greatestHits.TrackCount); // "43K views" is not a song count
        Assert.Equal("Paper Pretzel", greatestHits.Author?.Name);
        Assert.NotNull(greatestHits.Author?.BrowseId);
    }

    [Fact]
    public void Songs_filter_returns_one_titled_shelf_and_a_raw_continuation()
    {
        var response = ParserFixtures.Load("search_songs.json");
        var results = SearchParser.Parse(response, SearchFilter.Songs, _log);

        var shelf = Assert.Single(results.Sections);
        Assert.Equal("Songs", shelf.Title);
        Assert.Equal(20, shelf.Items.Count);
        Assert.All(shelf.Items, i => Assert.Equal(TrackType.Song, Assert.IsType<Track>(i).Type));

        var shelfRenderer = response.At("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 1, "musicShelfRenderer");
        Assert.Equal(shelfRenderer.At("continuations", 0, "nextContinuationData", "continuation").GetValue<string>(), results.Continuation);
        Assert.Null(results.TopResult);
    }

    [Fact]
    public void Songs_filter_first_row()
    {
        var first = Assert.IsType<Track>(SearchParser.Parse(ParserFixtures.Load("search_songs.json"), SearchFilter.Songs, _log).Sections[0].Items[0]);

        Assert.Equal("Instant Crush (feat. Julian Casablancas)", first.Title);
        Assert.Equal("khnokW3Mw24", first.VideoId);
        Assert.Equal([new ArtistRef("Daft Punk", DaftPunkId), new ArtistRef("Julian Casablancas", "UCWhpbdGLnR8XtXLr0yl2nYA")], first.Artists);
        Assert.Equal(new AlbumRef("Random Access Memories", "MPREb_K8qWMWVqXGi"), first.Album);
        Assert.Equal(TimeSpan.FromSeconds(338), first.Duration);
        Assert.Equal("1.2B plays", first.Views);
        Assert.False(first.IsExplicit);
        Assert.True(first.IsAvailable);
    }

    [Fact]
    public void Songs_continuation_page()
    {
        var results = SearchParser.ParseContinuation(ParserFixtures.Load("search_songs_continuation.json"), SearchFilter.Songs, _log);

        var shelf = Assert.Single(results.Sections);
        Assert.Equal(20, shelf.Items.Count);
        var first = Assert.IsType<Track>(shelf.Items[0]);
        Assert.Equal("Sea of Simulation", first.Title);
        Assert.Equal("_bz8rU7LdyE", first.VideoId);
        Assert.Equal(new AlbumRef("TRON: Legacy - The Complete Edition (Original Motion Picture Soundtrack)", "MPREb_3L0yrk0Vxg5"), first.Album);
        Assert.Equal(TimeSpan.FromSeconds(161), first.Duration);
        Assert.Equal("2M plays", first.Views);
        Assert.NotNull(results.Continuation);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Albums_filter()
    {
        var albums = SearchParser.Parse(ParserFixtures.Load("search_albums.json"), SearchFilter.Albums, _log)
            .Sections.Single(s => s.Title == "Albums").Items.Cast<Album>().ToList();

        Assert.Equal(20, albums.Count);
        Assert.Equal(1, albums.Count(a => a.Type == AlbumType.Album));
        Assert.Equal(19, albums.Count(a => a.Type == AlbumType.Single));

        var ram = albums[0];
        Assert.Equal("Random Access Memories", ram.Title);
        Assert.Equal("2013", ram.Year);
        Assert.Equal("MPREb_K8qWMWVqXGi", ram.BrowseId);
        Assert.Equal("OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c", ram.AudioPlaylistId);
        Assert.Equal([new ArtistRef("Daft Punk", DaftPunkId)], ram.Artists);

        Assert.Equal(["MPREb_lZBXOfVv2Tl", "MPREb_BAuNX37AYCB"], albums.Where(a => a.IsExplicit).Select(a => a.BrowseId));
        Assert.Equal([new ArtistRef("ZY", null)], albums[1].Artists); // unlinked artist run
    }

    [Fact]
    public void Artists_filter()
    {
        var results = SearchParser.Parse(ParserFixtures.Load("search_artists.json"), SearchFilter.Artists, _log);

        var artists = results.Sections.Single().Items.Cast<Artist>().ToList();
        Assert.Equal(
            ["Daft Punk", "The Weeknd", "The Strokes", "Pharrell", "Pentatonix", "Daft Punk's Karaoke Band", "Thomas Bangalter", "LCD Soundsystem"],
            artists.Select(a => a.Title));
        Assert.Equal(DaftPunkId, artists[0].BrowseId);
        Assert.Null(results.Continuation); // this shelf has no continuations
    }

    [Fact]
    public void Playlists_filter()
    {
        var results = SearchParser.Parse(ParserFixtures.Load("search_playlists.json"), SearchFilter.CommunityPlaylists, _log);

        var shelf = Assert.Single(results.Sections);
        Assert.Equal("Community playlists", shelf.Title);
        var playlists = shelf.Items.Cast<Playlist>().ToList();
        Assert.Equal(20, playlists.Count);
        Assert.Equal("Daft Punk Slow Songs", playlists[0].Title);
        Assert.Equal("Victor Reuss", playlists[0].Author?.Name);
        Assert.Equal("PLtT-b3qWIM-HP1gmNV1mZ2uUribKyo8qZ", playlists[0].PlaylistId); // search keeps "VL", the model does not
        Assert.All(playlists, p => Assert.Null(p.TrackCount)); // "3K views" is not a song count
        Assert.NotNull(results.Continuation);
    }

    [Fact]
    public void Shelf_of_another_type_is_dropped_by_a_filter()
    {
        var results = SearchParser.Parse(ParserFixtures.Load("search_songs.json"), SearchFilter.Albums, _log);

        Assert.Empty(results.Sections);
        Assert.Null(results.Continuation);
    }

    [Fact]
    public void Row_without_columns_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("search_songs.json");
        response.At("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 1,
            "musicShelfRenderer", "contents", 0, "musicResponsiveListItemRenderer").AsObject().Remove("flexColumns");

        var results = SearchParser.Parse(response, SearchFilter.Songs, _log);

        Assert.Equal(19, results.Sections[0].Items.Count);
        Assert.Equal("Get Lucky (feat. Pharrell Williams and Nile Rodgers)", results.Sections[0].Items[0].Title);
        Assert.Contains(_log.Warnings, w => w.Contains("musicResponsiveListItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Song_row_without_play_button_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("search_all.json");
        var sections = response.At("contents", "tabbedSearchResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents").AsArray();
        var voyager = sections.Select(s => s!["itemSectionRenderer"]?["contents"]?[0]?["musicResponsiveListItemRenderer"])
            .First(r => r?["flexColumns"]?[0]?["musicResponsiveListItemFlexColumnRenderer"]?["text"]?["runs"]?[0]?["text"]?.GetValue<string>() == "Voyager")!;
        voyager.AsObject().Remove("overlay");

        var items = SearchParser.Parse(response, SearchFilter.All, _log).Sections[0].Items;

        Assert.Equal(28, items.Count);
        Assert.DoesNotContain(items, i => i.Title == "Voyager");
        Assert.Contains(_log.Warnings, w => w.Contains("Voyager", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_empty_results()
    {
        var results = SearchParser.Parse(new JsonObject(), SearchFilter.All, _log);
        Assert.Null(results.TopResult);
        Assert.Empty(results.Sections);
        Assert.Empty(SearchParser.ParseContinuation(new JsonObject(), SearchFilter.Songs, _log).Sections);
    }
}
