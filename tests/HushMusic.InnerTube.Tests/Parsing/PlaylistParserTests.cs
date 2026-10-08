using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class PlaylistParserTests
{
    private const string PlaylistId = "PLw_8I7j6_QFogcNFA-ZgwnDz7X8rvnVUN";
    private const string FirstPageToken =
        "4qmFsgKHARIkVkxQTHdfOEk3ajZfUUZvZ2NORkEtWmd3bkR6N1g4cnZuVlVOGjplaDVRVkRwRFIxRnBSVVJSTlU1RWJFTlBWVkYzVDBSTk0xRlZSVEZSYWtHU0FRTUl1Z1R3QVFBJTNEmgIiUEx3XzhJN2o2X1FGb2djTkZBLVpnd25EejdYOHJ2blZVTg%3D%3D";

    private readonly CapturingLogger _log = new();

    [Fact]
    public void Public_playlist_header()
    {
        var page = PlaylistParser.Parse(ParserFixtures.Load("playlist.json"), PlaylistId, _log);

        var playlist = page.Playlist;
        Assert.Equal(PlaylistId, playlist.PlaylistId);
        Assert.Equal("All Daft Punk Songs In Order", playlist.Title);
        Assert.False(page.IsOwned);
        Assert.Equal(PrivacyStatus.Public, page.Privacy);
        Assert.Equal(new ArtistRef("IffyAlex", "UClff13Re9ULoAp1ZmjlWSPA"), playlist.Author);
        Assert.Equal("2023", page.Year);
        Assert.Equal(151, playlist.TrackCount); // "11K views • 151 tracks • 15+ hours"
        Assert.Equal("15+ hours", page.DurationText);
        Assert.StartsWith("All of daft punks songs (lives included)", playlist.Description);
        Assert.Equal([192, 576, 1200], playlist.Thumbnails.Select(t => t.Width));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void First_page_has_100_tracks_and_the_trailing_continuation_token()
    {
        var tracks = PlaylistParser.Parse(ParserFixtures.Load("playlist.json"), "VL" + PlaylistId, _log).Tracks;

        Assert.Equal(100, tracks.Items.Count);
        Assert.Equal(FirstPageToken, tracks.Continuation);

        var first = tracks.Items[0];
        Assert.Equal(("D2i0skatDaE", "DAFT PUNK -THE NEW WAVE"), (first.VideoId, first.Title));
        Assert.Equal([new ArtistRef("Tockyn", "UC1qGtCPZ93T1AmfIFtH3Q-A")], first.Artists);
        Assert.Null(first.Album);
        Assert.True(first.IsAvailable);
        Assert.Equal(TrackType.Video, first.Type); // MUSIC_VIDEO_TYPE_UGC
        Assert.Equal(TimeSpan.FromSeconds(438), first.Duration);
        Assert.NotNull(first.SetVideoId);

        var last = tracks.Items[99];
        Assert.Equal(("Nocturne", "-YQALJGxFsM", TrackType.Song), (last.Title, last.VideoId, last.Type));
        Assert.Equal(new AlbumRef("TRON: Legacy - The Complete Edition (Original Motion Picture Soundtrack)", "MPREb_3L0yrk0Vxg5"), last.Album);

        var explicitTrack = Assert.Single(tracks.Items, t => t.IsExplicit);
        Assert.Equal(("Aerodynamic (Slum Village Remix)", "ZEK8w1qV4x8"), (explicitTrack.Title, explicitTrack.VideoId));
        Assert.Equal(46, tracks.Items.ToList().IndexOf(explicitTrack));

        Assert.Equal(
            [new ArtistRef("Daft Punk", "UCRr1xG_2WIDs18a6cIiCxeA"), new ArtistRef("Gabrielle", "UC9ekV9MwdaKpQ_1dnoeePyA")],
            tracks.Items[84].Artists);
    }

    [Fact]
    public void Continuation_page_ends_with_a_greyed_out_row()
    {
        var page = PlaylistParser.ParseContinuation(ParserFixtures.Load("playlist_continuation.json"), _log);

        Assert.Equal(51, page.Items.Count);
        Assert.Null(page.Continuation);
        Assert.Equal(("End of Line", "-2C_P2N0LA8"), (page.Items[0].Title, page.Items[0].VideoId));
        Assert.Equal(TimeSpan.FromSeconds(157), page.Items[0].Duration);

        var greyed = page.Items[^1];
        Assert.False(greyed.IsAvailable);
        Assert.Equal("Daft Punk - Alive 1997 (Twitch Live Stream 2-22-2022)", greyed.Title);
        // ytmusicapi returns videoId None; the id is still in playlistItemData and Track.VideoId is required.
        Assert.Equal("606TA0HaSOQ", greyed.VideoId);
        Assert.Equal([new ArtistRef("Branmo's Archive", null)], greyed.Artists);
        Assert.Equal(TimeSpan.FromSeconds(5274), greyed.Duration); // "1:27:54"
        Assert.Null(greyed.LikeStatus);
        Assert.Equal(TrackType.Unknown, greyed.Type);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Both_pages_together_match_ytmusicapi_totals()
    {
        var first = PlaylistParser.Parse(ParserFixtures.Load("playlist.json"), PlaylistId, _log).Tracks.Items;
        var second = PlaylistParser.ParseContinuation(ParserFixtures.Load("playlist_continuation.json"), _log).Items;
        var all = first.Concat(second).ToList();

        Assert.Equal(151, all.Count);
        Assert.Equal(TimeSpan.FromSeconds(56970), all.TotalDuration());
        Assert.Equal(74, all.Count(t => t.Album is not null));
        Assert.Equal(74, all.Count(t => t.Type == TrackType.Song)); // ATV
        Assert.Equal(74, all.Count(t => t.Type == TrackType.Video)); // OMV 63 + UGC 11
        Assert.Equal(3, all.Count(t => t.Type == TrackType.Unknown)); // SHOULDER 2 + no type 1
        Assert.Equal(151, all.Select(t => t.SetVideoId).Distinct().Count());
    }

    [Fact]
    public void Owned_playlist_header_and_set_video_ids()
    {
        var page = PlaylistParser.Parse(ParserFixtures.Synthetic("synthetic_playlist_owned.json"), "PLsyntheticOwnedPlaylist", _log);

        Assert.True(page.IsOwned);
        Assert.Equal(PrivacyStatus.Private, page.Privacy);
        Assert.Equal("PLsyntheticOwnedPlaylist", page.Playlist.PlaylistId);
        Assert.Equal("All Daft Punk Songs In Order", page.Playlist.Title);
        Assert.Equal("2024", page.Year); // "Playlist • Private • 2024"
        Assert.Equal(3, page.Playlist.TrackCount);
        Assert.Equal("18 minutes", page.DurationText);

        Assert.Equal(3, page.Tracks.Items.Count);
        Assert.Equal("SYNTHETIC_SET_VIDEO_ID", page.Tracks.Items[0].SetVideoId);
        Assert.Equal("D2i0skatDaE", page.Tracks.Items[0].VideoId);
        Assert.NotNull(page.Tracks.Items[1].SetVideoId);
    }

    [Fact]
    public void Track_row_without_columns_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("playlist.json");
        response.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents", 0,
            "musicPlaylistShelfRenderer", "contents", 0, "musicResponsiveListItemRenderer").AsObject().Remove("flexColumns");

        var page = PlaylistParser.Parse(response, PlaylistId, _log);

        Assert.Equal(99, page.Tracks.Items.Count);
        Assert.Equal(FirstPageToken, page.Tracks.Continuation);
        Assert.Contains(_log.Warnings, w => w.Contains("musicResponsiveListItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_an_empty_page_with_the_requested_id()
    {
        var page = PlaylistParser.Parse(new JsonObject(), "VL" + PlaylistId, _log);
        Assert.Equal(PlaylistId, page.Playlist.PlaylistId);
        Assert.Empty(page.Tracks.Items);
        Assert.Null(page.Tracks.Continuation);
        Assert.Empty(PlaylistParser.ParseContinuation(new JsonObject(), _log).Items);
        Assert.NotEmpty(_log.Warnings);
    }
}
