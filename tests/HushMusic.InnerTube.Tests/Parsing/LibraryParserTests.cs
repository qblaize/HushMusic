using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

/// <summary>
/// Library pages need a signed-in session, so these use Fixtures/Synthetic/synthetic_*.json: the envelopes
/// ytmusicapi's library parsers expect, filled with renderer objects copied from the real fixtures
/// (see the "_synthetic" note in each file).
/// </summary>
public sealed class LibraryParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Playlists_skip_the_new_playlist_tile()
    {
        var page = LibraryParser.ParsePlaylists(ParserFixtures.Synthetic("synthetic_library_playlists.json"), _log);

        Assert.Equal(["Classical for Autumn", "Chill House"], page.Items.Take(2).Select(p => p.Title));
        Assert.Equal(3, page.Items.Count);
        Assert.Equal("RDCLAK5uy_nxpDij9qedpxoq-Fuzu_EC1PcFNB3txpY", page.Items[0].PlaylistId);
        Assert.Equal("SYNTHETIC_PLAYLISTS_TOKEN", page.Continuation);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Playlists_continuation()
    {
        var page = LibraryParser.ParsePlaylistsContinuation(ParserFixtures.Synthetic("synthetic_library_playlists_continuation.json"), _log);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal("Feel-Good Pop & Rock", page.Items[0].Title);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void Songs_drop_the_shuffle_all_row()
    {
        var page = LibraryParser.ParseSongs(ParserFixtures.Synthetic("synthetic_library_songs.json"), _log);

        Assert.Equal(3, page.Items.Count);
        Assert.Equal(("D2i0skatDaE", "DAFT PUNK -THE NEW WAVE"), (page.Items[0].VideoId, page.Items[0].Title));
        Assert.Equal("SYNTHETIC_SONGS_TOKEN", page.Continuation);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Songs_keep_the_first_row_when_it_is_a_song()
    {
        var response = ParserFixtures.Synthetic("synthetic_library_songs.json");
        ShelfContents(response, "musicShelfRenderer").RemoveAt(0); // no "Shuffle all" row this time

        var page = LibraryParser.ParseSongs(response, _log);

        Assert.Equal(3, page.Items.Count); // ytmusicapi would drop the first song here
        Assert.Equal("D2i0skatDaE", page.Items[0].VideoId);
    }

    [Fact]
    public void Songs_continuation()
    {
        var page = LibraryParser.ParseSongsContinuation(ParserFixtures.Synthetic("synthetic_library_songs_continuation.json"), _log);

        Assert.Equal(2, page.Items.Count);
        Assert.Equal("SYNTHETIC_SONGS_TOKEN_2", page.Continuation);
    }

    [Fact]
    public void Albums_and_continuation()
    {
        var page = LibraryParser.ParseAlbums(ParserFixtures.Synthetic("synthetic_library_albums.json"), _log);

        Assert.Equal(3, page.Items.Count);
        var first = page.Items[0];
        Assert.Equal(("GLBTM (Studio Outtakes)", "MPREb_X1DQ1j0PPrX", AlbumType.Single, "2023"), (first.Title, first.BrowseId, first.Type, first.Year));
        Assert.Equal("OLAK5uy_k29fGfk85tfMcdDEJPdinu2E9VgdHuCIU", first.AudioPlaylistId);
        Assert.Equal("SYNTHETIC_ALBUMS_TOKEN", page.Continuation);

        var next = LibraryParser.ParseAlbumsContinuation(ParserFixtures.Synthetic("synthetic_library_albums_continuation.json"), _log);
        Assert.Equal("Human After All (Remixes)", Assert.Single(next.Items).Title);
        Assert.Null(next.Continuation);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Artists_and_continuation()
    {
        var page = LibraryParser.ParseArtists(ParserFixtures.Synthetic("synthetic_library_artists.json"), _log);

        Assert.Equal(["Daft Punk", "The Weeknd", "The Strokes"], page.Items.Select(a => a.Title));
        Assert.Equal("UCRr1xG_2WIDs18a6cIiCxeA", page.Items[0].BrowseId);
        Assert.Equal("7.19M", page.Items[0].Subscribers);
        Assert.NotEmpty(page.Items[0].Thumbnails);
        Assert.Equal("SYNTHETIC_ARTISTS_TOKEN", page.Continuation);

        var next = LibraryParser.ParseArtistsContinuation(ParserFixtures.Synthetic("synthetic_library_artists_continuation.json"), _log);
        Assert.Equal("Pharrell", Assert.Single(next.Items).Title);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void History_groups_by_day()
    {
        var shelves = LibraryParser.ParseHistory(ParserFixtures.Synthetic("synthetic_history.json"), _log);

        Assert.Equal(["Today", "Yesterday"], shelves.Select(s => s.Title));
        Assert.Equal([2, 1], shelves.Select(s => s.Items.Count));
        Assert.Equal("D2i0skatDaE", Assert.IsType<Track>(shelves[0].Items[0]).VideoId);
    }

    [Fact]
    public void Signed_out_history_returns_no_shelves_and_warns()
    {
        var shelves = LibraryParser.ParseHistory(ParserFixtures.Synthetic("anon_signed_out_history.json"), _log);

        Assert.Empty(shelves);
        Assert.Contains(_log.Warnings, w => w.Contains("itemSectionRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Signed_out_library_tab_returns_an_empty_page()
    {
        var page = LibraryParser.ParsePlaylists(ParserFixtures.Synthetic("anon_signed_out_liked_playlists.json"), _log);

        Assert.Empty(page.Items);
        Assert.Null(page.Continuation);
        Assert.NotEmpty(_log.Warnings);
    }

    [Fact]
    public void Artist_row_without_browseId_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Synthetic("synthetic_library_artists.json");
        ShelfContents(response, "musicShelfRenderer")[0]!["musicResponsiveListItemRenderer"]!.AsObject().Remove("navigationEndpoint");

        var page = LibraryParser.ParseArtists(response, _log);

        Assert.Equal(["The Weeknd", "The Strokes"], page.Items.Select(a => a.Title));
        Assert.Contains(_log.Warnings, w => w.Contains("musicResponsiveListItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_empty_pages()
    {
        var empty = new JsonObject();
        Assert.Empty(LibraryParser.ParsePlaylists(empty, _log).Items);
        Assert.Empty(LibraryParser.ParsePlaylistsContinuation(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseSongs(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseSongsContinuation(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseAlbums(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseAlbumsContinuation(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseArtists(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseArtistsContinuation(empty, _log).Items);
        Assert.Empty(LibraryParser.ParseHistory(empty, _log));
    }

    private static JsonArray ShelfContents(JsonNode response, string renderer) =>
        response.At("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0,
            renderer, "contents").AsArray();
}
