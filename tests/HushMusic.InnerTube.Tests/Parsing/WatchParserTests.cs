using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class WatchParserTests
{
    private readonly CapturingLogger _log = new();

    [Theory]
    [InlineData("watch_playlist.json", "Drive", "vtuZmShWLGo")]
    [InlineData("watch_radio.json", "Fly Away", "2KeFjLDkOrI")]
    public void Queue_tracks_and_tab_browse_ids(string fixture, string lastTitle, string lastVideoId)
    {
        var queue = WatchParser.Parse(ParserFixtures.Load(fixture), _log);

        Assert.Equal(50, queue.Tracks.Count);
        Assert.Equal("RDAMVMhpSrLjc5SMs", queue.PlaylistId);
        Assert.Equal("MPLYt_PITqkpE6ExP-3", queue.LyricsBrowseId);
        Assert.Equal("MPTRt_PITqkpE6ExP-3", queue.RelatedBrowseId);
        Assert.Equal(("Don't Look Back in Anger", "X59TlszGtfM"), (queue.Tracks[1].Title, queue.Tracks[1].VideoId));
        Assert.Equal((lastTitle, lastVideoId), (queue.Tracks[^1].Title, queue.Tracks[^1].VideoId));
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void First_queue_track()
    {
        var track = WatchParser.Parse(ParserFixtures.Load("watch_playlist.json"), _log).Tracks[0];

        Assert.Equal(("hpSrLjc5SMs", "Wonderwall"), (track.VideoId, track.Title));
        Assert.Equal(TimeSpan.FromSeconds(259), track.Duration); // "4:19"
        Assert.Equal([new ArtistRef("Oasis", "UCmMUZbaYdNH0bEd1PAlAqsA")], track.Artists);
        Assert.Equal(new AlbumRef("(What's The Story) Morning Glory?", "MPREb_PITqkpE6ExP"), track.Album);
        Assert.Equal(TrackType.Song, track.Type);
        Assert.Null(track.LikeStatus); // signed out: the like toggle opens a sign-in modal
        Assert.Equal("RDAMVMhpSrLjc5SMs", track.PlaylistId);
        Assert.NotNull(track.SetVideoId);
        Assert.NotEmpty(track.Thumbnails);
    }

    [Fact]
    public void Radio_queue_diverges_from_the_plain_queue_at_index_11()
    {
        var plain = WatchParser.Parse(ParserFixtures.Load("watch_playlist.json"), _log).Tracks;
        var radio = WatchParser.Parse(ParserFixtures.Load("watch_radio.json"), _log).Tracks;

        var firstDifference = plain.Zip(radio).TakeWhile(p => p.First.VideoId == p.Second.VideoId).Count();
        Assert.Equal(11, firstDifference);
    }

    [Theory]
    [InlineData("watch_playlist.json")]
    [InlineData("watch_radio.json")]
    public void Radio_queues_return_the_raw_nextRadioContinuationData_token(string fixture)
    {
        var response = ParserFixtures.Load(fixture);
        var raw = Panel(response).At("continuations", 0, "nextRadioContinuationData", "continuation").GetValue<string>();

        Assert.Equal(raw, WatchParser.Parse(response, _log).Continuation);
    }

    [Theory]
    [InlineData("nextRadioContinuationData")]
    [InlineData("nextContinuationData")]
    public void Continuation_page_reads_either_token_style(string tokenKey)
    {
        // No continuation response was captured; this one reuses five real queue rows in the
        // continuationContents.playlistPanelContinuation shape ytmusicapi reads.
        var panel = Panel(ParserFixtures.Load("watch_radio.json"));
        var rows = new JsonArray(panel.At("contents").AsArray().Take(5).Select(r => r!.DeepClone()).ToArray());
        var response = new JsonObject
        {
            ["continuationContents"] = new JsonObject
            {
                ["playlistPanelContinuation"] = new JsonObject
                {
                    ["contents"] = rows,
                    ["continuations"] = new JsonArray(new JsonObject { [tokenKey] = new JsonObject { ["continuation"] = "NEXT_TOKEN" } }),
                },
            },
        };

        var page = WatchParser.ParseContinuation(response, _log);

        Assert.Equal(5, page.Items.Count);
        Assert.Equal("Wonderwall", page.Items[0].Title);
        Assert.Equal("NEXT_TOKEN", page.Continuation);
    }

    [Fact]
    public void Queue_row_without_videoId_is_skipped_with_a_warning()
    {
        var response = ParserFixtures.Load("watch_playlist.json");
        Panel(response).At("contents", 0, "playlistPanelVideoRenderer").AsObject().Remove("videoId");

        var queue = WatchParser.Parse(response, _log);

        Assert.Equal(49, queue.Tracks.Count);
        Assert.Equal("Don't Look Back in Anger", queue.Tracks[0].Title);
        Assert.Contains(_log.Warnings, w => w.Contains("playlistPanelVideoRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Empty_object_returns_an_empty_queue()
    {
        var queue = WatchParser.Parse(new JsonObject(), _log);
        Assert.Empty(queue.Tracks);
        Assert.Null(queue.Continuation);
        Assert.Empty(WatchParser.ParseContinuation(new JsonObject(), _log).Items);
    }

    private static JsonNode Panel(JsonNode response) =>
        response.At("contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer", "watchNextTabbedResultsRenderer", "tabs", 0,
            "tabRenderer", "content", "musicQueueRenderer", "content", "playlistPanelRenderer");
}
