using System.Text.Json;
using System.Text.Json.Serialization;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class MusicVideoTrackTests
{
    private static readonly Track Song = TestData.Track("song") with { Type = TrackType.Song };
    private static readonly Track Video = TestData.Track("video") with { Type = TrackType.Video };

    [Fact]
    public void Only_videos_have_their_own_picture()
    {
        Assert.True(Video.IsMusicVideo);
        Assert.False(Song.IsMusicVideo);
        Assert.False((Song with { Type = TrackType.Unknown }).IsMusicVideo);
        Assert.False((Song with { Type = TrackType.Episode }).IsMusicVideo);
        Assert.False(RadioData.Station().ToTrack().IsMusicVideo);
    }

    [Fact]
    public void A_song_with_a_video_counterpart_has_a_video_version()
    {
        Assert.Same(Video, (Song with { Counterpart = Video }).VideoVersion);
        Assert.Null(Song.VideoVersion);
        Assert.Null((Song with { Counterpart = Song with { VideoId = "other" } }).VideoVersion);

        // A video's counterpart is its song, never another picture.
        Assert.Null((Video with { Counterpart = Video with { VideoId = "other" } }).VideoVersion);
    }

    [Fact]
    public void Counterpart_survives_a_saved_session()
    {
        var options = new JsonSerializerOptions
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            IgnoreReadOnlyProperties = true,
            Converters = { new JsonStringEnumConverter() },
        };

        var json = JsonSerializer.Serialize(Song with { Counterpart = Video }, options);
        var restored = JsonSerializer.Deserialize<Track>(json, options)!;

        Assert.Equal("video", restored.VideoVersion?.VideoId);
        Assert.DoesNotContain("VideoVersion", json, StringComparison.Ordinal);
    }
}
