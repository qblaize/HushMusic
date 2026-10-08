using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

/// <summary>A discography long enough to page (Buckethead's albums: 100 cards, then 83 more).</summary>
public sealed class ArtistDiscographyParserTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void First_page_carries_a_grid_continuation()
    {
        var page = ArtistParser.ParseDiscography(ParserFixtures.Load("artist_albums_paged.json"), _log);

        Assert.Equal(12, page.Items.Count); // fixture trimmed from 100
        Assert.Equal(510, page.Continuation?.Length);
        var first = page.Items[0];
        Assert.Equal(("Metal Health", "MPREb_6bPc0A9DwcT", "OLAK5uy_mxtestrrBFzzC7aHEAhgbNy9M-xLkhALM"), (first.Title, first.BrowseId, first.AudioPlaylistId));
        Assert.Equal((AlbumType.Album, "2026"), (first.Type, first.Year));
        Assert.All(page.Items, a => Assert.Equal(AlbumType.Album, a.Type));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Continuation_page_is_the_end()
    {
        var page = ArtistParser.ParseDiscographyContinuation(ParserFixtures.Load("artist_albums_continuation.json"), _log);

        Assert.Equal(10, page.Items.Count); // fixture trimmed from 83
        Assert.Null(page.Continuation);
        Assert.Equal(("Pilot", "MPREb_p2YqUdkViLz", "2014"), (page.Items[0].Title, page.Items[0].BrowseId, page.Items[0].Year));
        Assert.Equal(("Infinity Hill", "MPREb_iSC6YwnELmA", "OLAK5uy_l7e-juyk5T6FgN4-diEj2YkCo0L8axmUg"), (page.Items[^1].Title, page.Items[^1].BrowseId, page.Items[^1].AudioPlaylistId));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Continuation_without_grid_is_empty_and_logged()
    {
        var page = ArtistParser.ParseDiscographyContinuation(System.Text.Json.Nodes.JsonNode.Parse("""{"continuationContents":{}}""")!, _log);

        Assert.Empty(page.Items);
        Assert.Null(page.Continuation);
        Assert.Single(_log.Warnings);
    }
}
