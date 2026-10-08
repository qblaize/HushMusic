using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class AlbumParserTests
{
    private const string RamId = "MPREb_K8qWMWVqXGi";
    private static readonly ArtistRef DaftPunk = new("Daft Punk", "UCRr1xG_2WIDs18a6cIiCxeA");

    private readonly CapturingLogger _log = new();

    [Fact]
    public void Album_header()
    {
        var page = AlbumParser.Parse(ParserFixtures.Load("album.json"), RamId, _log);

        var album = page.Album;
        Assert.Equal("Random Access Memories", album.Title);
        Assert.Equal(RamId, album.BrowseId);
        Assert.Equal(AlbumType.Album, album.Type);
        Assert.Equal("2013", album.Year);
        Assert.Equal([DaftPunk], album.Artists);
        Assert.False(album.IsExplicit);
        Assert.Equal("OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c", album.AudioPlaylistId);
        Assert.Equal(4, album.Thumbnails.Count);
        Assert.Equal((544, 544), (album.BestThumbnail!.Width, album.BestThumbnail.Height));
        Assert.Equal(13, page.TrackCount);
        Assert.Equal("1 hour, 14 minutes", page.DurationText);
        Assert.False(string.IsNullOrWhiteSpace(page.Description));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Album_tracks()
    {
        var page = AlbumParser.Parse(ParserFixtures.Load("album.json"), RamId, _log);

        Assert.Equal(13, page.Tracks.Count);
        Assert.Equal(TimeSpan.FromSeconds(4483), page.Tracks.TotalDuration());

        var first = page.Tracks[0];
        Assert.Equal("Give Life Back to Music", first.Title);
        Assert.Equal("IluRBvnYMoY", first.VideoId);
        Assert.Equal(TimeSpan.FromSeconds(276), first.Duration);
        Assert.Equal("54M plays", first.Views);
        // Anonymous album rows carry the official video (OMV), not the audio track.
        Assert.Equal(TrackType.Video, first.Type);
        Assert.Equal(new AlbumRef("Random Access Memories", RamId), first.Album);
        Assert.Equal([DaftPunk], first.Artists);
        Assert.Equal("OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c", first.PlaylistId);

        var last = page.Tracks[12];
        Assert.Equal(("Contact", "ZbbTmR6Xaag"), (last.Title, last.VideoId));
        Assert.Equal(TimeSpan.FromSeconds(384), last.Duration);
    }

    [Fact]
    public void Album_other_versions_use_the_medium_carousel_only()
    {
        var page = AlbumParser.Parse(ParserFixtures.Load("album.json"), RamId, _log);

        var version = Assert.Single(page.OtherVersions);
        Assert.Equal("Random Access Memories (10th Anniversary Edition)", version.Title);
        Assert.Equal("MPREb_eMBjPmWySjR", version.BrowseId);
    }

    [Fact]
    public void Single_with_repeated_videoIds()
    {
        var page = AlbumParser.Parse(ParserFixtures.Load("album_single.json"), "MPREb_X1DQ1j0PPrX", _log);

        Assert.Equal("GLBTM (Studio Outtakes)", page.Album.Title);
        Assert.Equal(AlbumType.Single, page.Album.Type);
        Assert.Equal("2023", page.Album.Year);
        Assert.Equal([DaftPunk], page.Album.Artists);
        Assert.Equal("OLAK5uy_k29fGfk85tfMcdDEJPdinu2E9VgdHuCIU", page.Album.AudioPlaylistId);
        Assert.Equal(3, page.TrackCount);
        Assert.Equal("13 minutes, 37 seconds", page.DurationText);
        Assert.Equal(TimeSpan.FromSeconds(817), page.Tracks.TotalDuration());
        Assert.Empty(page.OtherVersions);

        Assert.Equal(["GLBTM (Studio Outtakes)", "GLBTM (Studio Outtakes) [Edit]", "Give Life Back to Music"], page.Tracks.Select(t => t.Title));
        Assert.Equal(["YiZfLvLU5Jc", "YiZfLvLU5Jc", "IluRBvnYMoY"], page.Tracks.Select(t => t.VideoId));
        Assert.Equal([382, 159, 276], page.Tracks.Select(t => (int)t.Duration!.Value.TotalSeconds));

        // Same videoId twice: rows are told apart by their playlistSetVideoId.
        Assert.All(page.Tracks, t => Assert.NotNull(t.SetVideoId));
        Assert.NotEqual(page.Tracks[0].SetVideoId, page.Tracks[1].SetVideoId);
    }

    [Fact]
    public void Track_row_without_columns_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("album.json");
        response.At("contents", "twoColumnBrowseResultsRenderer", "secondaryContents", "sectionListRenderer", "contents", 0,
            "musicShelfRenderer", "contents", 0, "musicResponsiveListItemRenderer").AsObject().Remove("flexColumns");

        var page = AlbumParser.Parse(response, RamId, _log);

        Assert.Equal(12, page.Tracks.Count);
        Assert.Equal("The Game of Love", page.Tracks[0].Title);
        Assert.Contains(_log.Warnings, w => w.Contains("musicResponsiveListItemRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_header_still_returns_the_tracks()
    {
        var response = ParserFixtures.Load("album.json");
        response.At("contents", "twoColumnBrowseResultsRenderer", "tabs", 0, "tabRenderer", "content", "sectionListRenderer", "contents", 0)
            .AsObject().Remove("musicResponsiveHeaderRenderer");

        var page = AlbumParser.Parse(response, RamId, _log);

        Assert.Equal(string.Empty, page.Album.Title);
        Assert.Equal(13, page.Tracks.Count);
        Assert.Contains(_log.Warnings, w => w.Contains("album header", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_an_empty_album()
    {
        var page = AlbumParser.Parse(new JsonObject(), RamId, _log);
        Assert.Equal(RamId, page.Album.BrowseId);
        Assert.Empty(page.Tracks);
        Assert.Empty(page.OtherVersions);
    }
}
