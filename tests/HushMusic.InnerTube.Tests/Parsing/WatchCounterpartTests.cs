using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

/// <summary>
/// The Song / Video switch: queue rows wrapped in <c>playlistPanelVideoWrapperRenderer</c> (ytmusicapi
/// parse_watch_playlist). Anonymous responses carry no wrapped rows (checked on several seeds), so these tests wrap
/// real rows of watch_playlist.json in the shape ytmusicapi reads.
/// </summary>
public sealed class WatchCounterpartTests
{
    private readonly CapturingLogger _log = new();

    [Fact]
    public void Wrapped_row_parses_the_primary_renderer_with_its_counterpart()
    {
        var response = ParserFixtures.Load("watch_playlist.json");
        var rows = Panel(response).At("contents").AsArray();
        var video = rows[1]!.At("playlistPanelVideoRenderer").DeepClone();
        video.At("navigationEndpoint", "watchEndpoint", "watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig")["musicVideoType"] =
            "MUSIC_VIDEO_TYPE_OMV";
        rows[0] = Wrap(rows[0]!.At("playlistPanelVideoRenderer").DeepClone(), video);

        var queue = WatchParser.Parse(response, _log);

        Assert.Equal(50, queue.Tracks.Count);
        var track = queue.Tracks[0];
        Assert.Equal(("hpSrLjc5SMs", "Wonderwall", TrackType.Song), (track.VideoId, track.Title, track.Type));
        var counterpart = Assert.IsType<Track>(track.Counterpart);
        Assert.Equal(("X59TlszGtfM", "Don't Look Back in Anger", TrackType.Video), (counterpart.VideoId, counterpart.Title, counterpart.Type));
        Assert.Equal([new ArtistRef("Oasis", "UCmMUZbaYdNH0bEd1PAlAqsA")], counterpart.Artists);
        Assert.Null(counterpart.Counterpart);
        Assert.Null(queue.Tracks[1].Counterpart);
        Assert.Equal("RDAMVMhpSrLjc5SMs", queue.PlaylistId);
        Assert.Empty(_log.Warnings);
    }

    [Fact]
    public void Plain_rows_have_no_counterpart()
    {
        var queue = WatchParser.Parse(ParserFixtures.Load("watch_playlist.json"), _log);

        Assert.All(queue.Tracks, t => Assert.Null(t.Counterpart));
    }

    [Fact]
    public void Unreadable_counterpart_keeps_the_primary_track()
    {
        var response = ParserFixtures.Load("watch_playlist.json");
        var rows = Panel(response).At("contents").AsArray();
        var broken = rows[1]!.At("playlistPanelVideoRenderer").DeepClone();
        broken.AsObject().Remove("videoId");
        rows[0] = Wrap(rows[0]!.At("playlistPanelVideoRenderer").DeepClone(), broken);

        var track = WatchParser.Parse(response, _log).Tracks[0];

        Assert.Equal("hpSrLjc5SMs", track.VideoId);
        Assert.Null(track.Counterpart);
        Assert.Contains(_log.Warnings, w => w.Contains("playlistPanelVideoRenderer", StringComparison.Ordinal));
    }

    [Fact]
    public void Continuation_rows_keep_their_counterpart()
    {
        var rows = Panel(ParserFixtures.Load("watch_radio.json")).At("contents").AsArray();
        var wrapped = Wrap(rows[0]!.At("playlistPanelVideoRenderer").DeepClone(), rows[2]!.At("playlistPanelVideoRenderer").DeepClone());
        var response = new JsonObject
        {
            ["continuationContents"] = new JsonObject
            {
                ["playlistPanelContinuation"] = new JsonObject { ["contents"] = new JsonArray(wrapped) },
            },
        };

        var page = WatchParser.ParseContinuation(response, _log);

        Assert.Equal(rows[2]!.At("playlistPanelVideoRenderer", "videoId").GetValue<string>(), Assert.Single(page.Items).Counterpart?.VideoId);
    }

    private static JsonObject Wrap(JsonNode primary, JsonNode counterpart) => new()
    {
        ["playlistPanelVideoWrapperRenderer"] = new JsonObject
        {
            ["primaryRenderer"] = new JsonObject { ["playlistPanelVideoRenderer"] = primary },
            ["counterpart"] = new JsonArray(new JsonObject
            {
                ["counterpartRenderer"] = new JsonObject { ["playlistPanelVideoRenderer"] = counterpart },
            }),
        },
    };

    private static JsonNode Panel(JsonNode response) =>
        response.At("contents", "singleColumnMusicWatchNextResultsRenderer", "tabbedRenderer", "watchNextTabbedResultsRenderer", "tabs", 0,
            "tabRenderer", "content", "musicQueueRenderer", "content", "playlistPanelRenderer");
}
