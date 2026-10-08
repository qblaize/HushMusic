using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;

namespace HushMusic.InnerTube.Parsing;

/// <summary>
/// Signed-in library pages (ytmusicapi mixins/library.py, parsers/library.py). No real responses were captured
/// yet (they need a signed-in session); the shapes follow ytmusicapi and the item renderers are the same ones
/// used on public pages. Continuation pages are classic: <c>continuationContents.gridContinuation</c> for grids,
/// <c>continuationContents.musicShelfContinuation</c> for lists.
/// </summary>
internal static class LibraryParser
{
    private const string Page = "Library";
    private const string Grid = "gridRenderer";
    private const string MusicShelf = "musicShelfRenderer";

    /// <summary><c>FEmusic_liked_playlists</c>: a grid whose first card is the "New playlist" tile.</summary>
    public static Paged<Playlist> ParsePlaylists(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var grid = GetLibraryContents(response, Grid, scope);
        return grid is null ? Paged<Playlist>.Empty : new Paged<Playlist>(ParsePlaylistCards(grid.Arr("items"), scope), Continuations.Classic(grid));
    }

    public static Paged<Playlist> ParsePlaylistsContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var grid = GetContinuation(response, "gridContinuation", scope);
        return grid is null ? Paged<Playlist>.Empty : new Paged<Playlist>(ParsePlaylistCards(grid.Arr("items"), scope), Continuations.Classic(grid));
    }

    /// <summary><c>FEmusic_liked_videos</c> (songs saved to the library; not the same as liked songs <c>VLLM</c>).</summary>
    public static Paged<Track> ParseSongs(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var shelf = GetLibraryContents(response, MusicShelf, scope);
        if (shelf is null)
        {
            return Paged<Track>.Empty;
        }

        var rows = shelf.Arr("contents").Objects().ToList();

        // ytmusicapi drops the first row whenever there are two or more: it is the "Shuffle all" entry.
        // It is only dropped here when it really has no video, so a missing shuffle row never costs a song.
        if (rows.Count >= 2 && !HasVideo(rows[0]))
        {
            scope.IgnoreItem(PlaylistItemParser.RendererName, "\"Shuffle all\" row");
            rows.RemoveAt(0);
        }

        return new Paged<Track>(PlaylistItemParser.ParseRows(rows, scope), Continuations.Classic(shelf));
    }

    public static Paged<Track> ParseSongsContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var shelf = GetContinuation(response, "musicShelfContinuation", scope);
        return shelf is null ? Paged<Track>.Empty : new Paged<Track>(PlaylistItemParser.ParseRows(shelf.Arr("contents"), scope), Continuations.Classic(shelf));
    }

    /// <summary><c>FEmusic_liked_albums</c>: a grid of album cards.</summary>
    public static Paged<Album> ParseAlbums(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var grid = GetLibraryContents(response, Grid, scope);
        return grid is null ? Paged<Album>.Empty : new Paged<Album>(ParseAlbumCards(grid.Arr("items"), scope), Continuations.Classic(grid));
    }

    public static Paged<Album> ParseAlbumsContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var grid = GetContinuation(response, "gridContinuation", scope);
        return grid is null ? Paged<Album>.Empty : new Paged<Album>(ParseAlbumCards(grid.Arr("items"), scope), Continuations.Classic(grid));
    }

    /// <summary><c>FEmusic_library_corpus_track_artists</c> (and subscriptions, <c>FEmusic_library_corpus_artists</c>).</summary>
    public static Paged<Artist> ParseArtists(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var shelf = GetLibraryContents(response, MusicShelf, scope);
        return shelf is null ? Paged<Artist>.Empty : new Paged<Artist>(ParseArtistRows(shelf.Arr("contents"), scope), Continuations.Classic(shelf));
    }

    public static Paged<Artist> ParseArtistsContinuation(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, Page);
        var shelf = GetContinuation(response, "musicShelfContinuation", scope);
        return shelf is null ? Paged<Artist>.Empty : new Paged<Artist>(ParseArtistRows(shelf.Arr("contents"), scope), Continuations.Classic(shelf));
    }

    /// <summary>
    /// <c>FEmusic_history</c>: one shelf per day ("Today", "Yesterday"...). ytmusicapi raises on any section without
    /// a track shelf (e.g. a sign-in prompt); here such sections are skipped with a warning.
    /// </summary>
    public static IReadOnlyList<Shelf> ParseHistory(JsonNode response, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(response);
        var scope = new ParseScope(logger, "History");

        var sections = response.Arr("contents", "singleColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents");
        if (sections is null)
        {
            scope.MissingStructure("contents.singleColumnBrowseResultsRenderer.tabs[0].tabRenderer.content.sectionListRenderer.contents");
            return [];
        }

        var shelves = new List<Shelf>();
        foreach (var section in sections.Objects())
        {
            if (section.Obj(MusicShelf) is not { } shelf)
            {
                var message = TextRuns.Text(section.Obj("musicNotifierShelfRenderer", "title"));
                scope.SkipItem(section.Describe(), message is null ? "history section without a track shelf" : $"history section without a track shelf: {message}");
                continue;
            }

            shelves.Add(new Shelf
            {
                Title = shelf.Str("title", "runs", 0, "text") ?? string.Empty,
                Items = PlaylistItemParser.ParseRows(shelf.Arr("contents"), scope),
            });
        }

        return shelves;
    }

    /// <summary>Album cards of library and artist discography grids (ytmusicapi parse_albums).</summary>
    internal static List<Album> ParseAlbumCards(JsonArray? items, ParseScope scope)
    {
        var albums = new List<Album>();
        foreach (var entry in items.Objects())
        {
            if (entry.Obj(TwoRowItemParser.RendererName) is not { } card)
            {
                scope.SkipItem(entry.Describe(), "grid item is not an album card");
                continue;
            }

            if (TwoRowItemParser.ParseAlbum(card, scope) is { } album)
            {
                albums.Add(album);
            }
        }

        return albums;
    }

    private static List<Playlist> ParsePlaylistCards(JsonArray? items, ParseScope scope)
    {
        var playlists = new List<Playlist>();
        foreach (var entry in items.Objects())
        {
            if (entry.Obj(TwoRowItemParser.RendererName) is not { } card)
            {
                scope.SkipItem(entry.Describe(), "grid item is not a playlist card");
                continue;
            }

            // The "New playlist" tile has no browse link; ytmusicapi drops items[0] blindly.
            if (TextRuns.BrowseId(card.Obj("title", "runs", 0)) is null && card.Str("navigationEndpoint", "browseEndpoint", "browseId") is null)
            {
                scope.IgnoreItem(TwoRowItemParser.RendererName, "card without a playlist link (\"New playlist\" tile)");
                continue;
            }

            if (TwoRowItemParser.ParsePlaylist(card, scope) is { } playlist)
            {
                playlists.Add(playlist);
            }
        }

        return playlists;
    }

    /// <summary>Artist rows (ytmusicapi parse_artists).</summary>
    private static List<Artist> ParseArtistRows(JsonArray? rows, ParseScope scope)
    {
        var artists = new List<Artist>();
        foreach (var entry in rows.Objects())
        {
            if (entry.Obj(PlaylistItemParser.RendererName) is not { } row)
            {
                scope.SkipItem(entry.Describe(), "not an artist row");
                continue;
            }

            var browseId = row.Str("navigationEndpoint", "browseEndpoint", "browseId");
            var name = Columns.FlexText(row, 0);
            if (browseId is null || name is null)
            {
                scope.SkipItem(PlaylistItemParser.RendererName, "artist row without name or browseId");
                continue;
            }

            artists.Add(new Artist
            {
                Title = name,
                BrowseId = browseId,
                Thumbnails = Thumbnails.OfResponsive(row),
                // "1.2M subscribers" / "12 songs" -> first token, as ytmusicapi does.
                Subscribers = TextRuns.FirstToken(Columns.FlexText(row, 1)),
            });
        }

        return artists;
    }

    /// <summary>
    /// ytmusicapi get_library_contents: the grid/shelf is either the first section, the first item of an
    /// itemSectionRenderer, or (empty library) on the second/third tab.
    /// </summary>
    private static JsonObject? GetLibraryContents(JsonNode response, string renderer, ParseScope scope)
    {
        var tabs = response.Arr("contents", "singleColumnBrowseResultsRenderer", "tabs");
        var sections = tabs.Arr(0, "tabRenderer", "content", "sectionListRenderer", "contents");
        JsonObject? contents;
        if (sections is null)
        {
            // Empty library: non-premium accounts have no downloads tab, so the library tab moves.
            var libraryTab = (tabs?.Count ?? 0) < 3 ? 1 : 2;
            contents = tabs.Obj(libraryTab, "tabRenderer", "content", "sectionListRenderer", "contents", 0, renderer);
        }
        else
        {
            var itemSection = sections.Objects().Select(s => s.Obj("itemSectionRenderer")).FirstOrDefault(s => s is not null);
            contents = itemSection is null
                ? sections.Obj(0, renderer)
                : itemSection.Obj("contents", 0, renderer);
        }

        if (contents is null)
        {
            scope.MissingStructure($"library {renderer}");
        }

        return contents;
    }

    private static JsonObject? GetContinuation(JsonNode response, string type, ParseScope scope)
    {
        var contents = response.Obj("continuationContents", type);
        if (contents is null)
        {
            scope.MissingStructure($"continuationContents.{type}");
        }

        return contents;
    }

    private static bool HasVideo(JsonObject row)
    {
        var item = row.Obj(PlaylistItemParser.RendererName);
        return item.Str("playlistItemData", "videoId") is not null
            || item.Str("overlay", "musicItemThumbnailOverlayRenderer", "content", "musicPlayButtonRenderer", "playNavigationEndpoint", "watchEndpoint", "videoId") is not null
            || Columns.FlexRun(item, 0).Str("navigationEndpoint", "watchEndpoint", "videoId") is not null;
    }
}
