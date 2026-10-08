using System.Text.Json.Nodes;
using HushMusic.Core.Models;
using HushMusic.InnerTube.Parsing.Common;
using Xunit;

namespace HushMusic.InnerTube.Tests.Parsing;

public sealed class CommonParsingTests
{
    [Theory]
    [InlineData("4:36", 276)]
    [InlineData("1:27:54", 5274)]
    [InlineData("0:07", 7)]
    [InlineData(" 3:45 ", 225)]
    public void Duration_text_is_parsed(string text, int seconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(seconds), TextRuns.ParseDuration(text));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("2,343")]
    [InlineData("1 hour, 14 minutes")]
    [InlineData("4:")]
    [InlineData("99999999999:00")]
    public void Non_duration_text_is_null(string? text)
    {
        Assert.Null(TextRuns.ParseDuration(text));
    }

    [Theory]
    [InlineData("52M plays", true)]
    [InlineData("1.2B views", true)]
    [InlineData("2Pac", false)] // ASCII token without a space is an artist name
    [InlineData("Daft Punk", false)]
    [InlineData("5 Seconds of Summer", true)] // ytmusicapi has the same ambiguity
    [InlineData("Дружки", false)]
    public void Views_text_detection_follows_ytmusicapi(string text, bool isViews)
    {
        Assert.Equal(isViews, TextRuns.IsViewsText(text));
    }

    [Fact]
    public void Song_runs_skip_the_type_word_and_classify_the_rest()
    {
        var runs = Runs(
            """[{"text":"Song"},{"text":" • "},{"text":"Daft Punk","navigationEndpoint":{"browseEndpoint":{"browseId":"UCRr1xG_2WIDs18a6cIiCxeA"}}},""" +
            """{"text":" • "},{"text":"Random Access Memories","navigationEndpoint":{"browseEndpoint":{"browseId":"MPREb_K8qWMWVqXGi"}}},""" +
            """{"text":" • "},{"text":"5:38"},{"text":" • "},{"text":"2013"},{"text":" • "},{"text":"1.2B plays"}]""");

        var info = TextRuns.ParseSongRuns(runs, skipTypeSpec: true);

        Assert.Equal([new ArtistRef("Daft Punk", "UCRr1xG_2WIDs18a6cIiCxeA")], info.Artists);
        Assert.Equal(new AlbumRef("Random Access Memories", "MPREb_K8qWMWVqXGi"), info.Album);
        Assert.Equal(TimeSpan.FromSeconds(338), info.Duration);
        Assert.Equal("2013", info.Year);
        Assert.Equal("1.2B plays", info.Views);
    }

    [Fact]
    public void Without_skipping_the_type_word_becomes_an_artist()
    {
        var runs = Runs("""[{"text":"Song"},{"text":" • "},{"text":"Daft Punk"}]""");

        Assert.Equal(["Song", "Daft Punk"], TextRuns.ParseSongRuns(runs).Artists.Select(a => a.Name));
        Assert.Equal(["Daft Punk"], TextRuns.ParseSongRuns(runs, skipTypeSpec: true).Artists.Select(a => a.Name));
    }

    [Fact]
    public void Artist_runs_tolerate_an_even_length_list()
    {
        var runs = Runs("""[{"text":"A"},{"text":" & "},{"text":"B"},{"text":", "}]""");

        Assert.Equal(["A", "B"], TextRuns.ParseArtistsRuns(runs).Select(a => a.Name));
    }

    [Theory]
    [InlineData("MUSIC_VIDEO_TYPE_ATV", TrackType.Song)]
    [InlineData("MUSIC_VIDEO_TYPE_PRIVATELY_OWNED_TRACK", TrackType.Song)]
    [InlineData("MUSIC_VIDEO_TYPE_OMV", TrackType.Video)]
    [InlineData("MUSIC_VIDEO_TYPE_UGC", TrackType.Video)]
    [InlineData("MUSIC_VIDEO_TYPE_OFFICIAL_SOURCE_MUSIC", TrackType.Video)]
    [InlineData("MUSIC_VIDEO_TYPE_PODCAST_EPISODE", TrackType.Episode)]
    [InlineData("MUSIC_VIDEO_TYPE_SHOULDER", TrackType.Unknown)]
    public void Video_type_maps_to_track_type(string videoType, TrackType expected)
    {
        Assert.Equal(expected, VideoTypes.ToTrackType(videoType, TrackType.Song));
    }

    [Fact]
    public void Navigation_is_null_safe()
    {
        var node = JsonNode.Parse("""{"a":[{"b":"x"},{"b":7}],"n":"22150"}""");

        Assert.Equal("x", node.Str("a", 0, "b"));
        Assert.Equal(7L, node.Long("a", -1, "b"));
        Assert.Equal(22150L, node.Long("n")); // numbers sent as strings
        Assert.Null(node.Str("a", 1, "b")); // a number is not a string
        Assert.Null(node.Str("a", 5, "b"));
        Assert.Null(node.Str("a", "b"));
        Assert.Null(node.Nav("n", 0));
        Assert.Null(((JsonNode?)null).Nav("a"));
    }

    [Theory]
    [InlineData("Album", AlbumType.Album)]
    [InlineData("Single", AlbumType.Single)]
    [InlineData("EP", AlbumType.EP)]
    [InlineData("Audiobook", AlbumType.Unknown)]
    public void Album_type_words(string text, AlbumType expected)
    {
        Assert.Equal(expected, EnglishText.ParseAlbumType(text));
    }

    private static JsonArray Runs(string json) => JsonNode.Parse(json)!.AsArray();
}
