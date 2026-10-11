using System.Text.Json.Nodes;
using HushMusic.Core.Abstractions;
using HushMusic.Playback.YtDlp;
using Xunit;

namespace HushMusic.Playback.Tests;

public sealed class VideoFormatTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);

    // The shape of yt-dlp's -j output (trimmed): the picked audio format merged at the top, every format in "formats".
    // Parsed from text, as the real output is.
    private static JsonObject Info() => JsonNode.Parse(InfoObject().ToJsonString())!.AsObject();

    private static JsonObject InfoObject() => new()
    {
        ["id"] = "dQw4w9WgXcQ",
        ["duration"] = 213,
        ["url"] = Url(251),
        ["ext"] = "webm",
        ["acodec"] = "opus",
        ["vcodec"] = "none",
        ["abr"] = 129.5,
        ["http_headers"] = new JsonObject { ["User-Agent"] = "Mozilla/5.0" },
        ["formats"] = new JsonArray(
            Format("sb0", "mhtml", "images", "none", 320, 180, protocol: "mhtml"),
            Format("251", "webm", "none", "opus", null, null),
            Format("140", "m4a", "none", "mp4a.40.2", null, null),
            Format("278", "webm", "vp9", "none", 256, 144, fps: 25, tbr: 58),
            Format("243", "webm", "vp9", "none", 640, 360, fps: 25, tbr: 226),
            Format("136", "mp4", "avc1.4D401F", "none", 1280, 720, fps: 25, tbr: 993),
            Format("398", "mp4", "av01.0.05M.08", "none", 1280, 720, fps: 25, tbr: 661),
            Format("247", "webm", "vp9", "none", 1280, 720, fps: 25, tbr: 664),
            Format("302", "webm", "vp09.00.40.08", "none", 1280, 720, fps: 50, tbr: 1100),
            Format("248", "webm", "vp9", "none", 1920, 1080, fps: 25, tbr: 1158),
            Format("335", "webm", "vp09.02.40.10.01.09.16.09.00", "none", 1920, 1080, fps: 25, tbr: 2200, dynamicRange: "HDR10"),
            Format("271", "webm", "vp9", "none", 2560, 1440, fps: 25, tbr: 5674),
            Format("18", "mp4", "avc1.42001E", "mp4a.40.2", 640, 360, fps: 25, tbr: 500)),
    };

    [Fact]
    public void Only_sdr_vp9_webm_video_only_formats_up_to_1080p_are_listed()
    {
        var formats = YtDlpOutput.ParseVideoFormats("dQw4w9WgXcQ", Info(), Now);

        Assert.Equal(["144x256", "360x640", "720x1280", "720x1280", "1080x1920"], formats.Select(f => $"{f.Height}x{f.Width}"));
        Assert.All(formats, f => Assert.Equal("webm", f.Container));
        Assert.Equal(["vp9", "vp9", "vp9", "vp09.00.40.08", "vp9"], formats.Select(f => f.Codec));
    }

    [Fact]
    public void Video_format_carries_size_rate_duration_expiry_and_headers()
    {
        var format = YtDlpOutput.ParseVideoFormats("dQw4w9WgXcQ", Info(), Now).Single(f => f.Url.Query.Contains("itag=247", StringComparison.Ordinal));

        Assert.Equal("dQw4w9WgXcQ", format.VideoId);
        Assert.Equal((1280, 720, 25.0, 664), (format.Width, format.Height, format.FrameRate, format.BitrateKbps));
        Assert.Equal(TimeSpan.FromSeconds(213), format.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791000000), format.ExpiresAt);
        Assert.Equal("Mozilla/5.0", format.Headers["User-Agent"]);
        Assert.False(format.IsLive);
    }

    [Fact]
    public void Audio_stream_is_unchanged_by_the_video_formats()
    {
        var stream = YtDlpOutput.ParseStream("dQw4w9WgXcQ", Info(), Now);

        Assert.Equal(("webm", "opus", 130), (stream.Container, stream.Codec, stream.BitrateKbps));
        Assert.Contains("itag=251", stream.Url.Query, StringComparison.Ordinal);
        Assert.Null(stream.Width);
    }

    [Fact]
    public void Drm_and_missing_formats_give_no_video()
    {
        var info = Info();
        foreach (var format in info["formats"]!.AsArray().OfType<JsonObject>())
        {
            format["has_drm"] = true;
        }

        Assert.Empty(YtDlpOutput.ParseVideoFormats("x", info, Now));
        Assert.Empty(YtDlpOutput.ParseVideoFormats("x", new JsonObject { ["duration"] = 10 }, Now));
    }

    [Fact]
    public void ParseInfo_reads_the_last_json_line()
    {
        var info = YtDlpOutput.ParseInfo("dQw4w9WgXcQ", "[youtube] Extracting URL\n" + Info().ToJsonString() + "\n");

        Assert.Equal(13, info["formats"]!.AsArray().Count);
    }

    [Theory]
    [InlineData(0, 720, 720)]
    [InlineData(-1, 1080, 1080)]
    [InlineData(400, 720, 360)] // 400 px tall: 360p, upscaled by 10%
    [InlineData(420, 720, 480)]
    [InlineData(600, 720, 720)]
    [InlineData(300, 720, 360)]
    [InlineData(840, 720, 720)] // capped
    [InlineData(840, 1080, 1080)]
    [InlineData(5000, 4320, 1080)] // never above 1080p
    [InlineData(100, 720, 144)]
    public void HeightFor_picks_the_quality_step_for_the_size_on_screen(double pixels, int cap, int expected) =>
        Assert.Equal(expected, VideoFormats.HeightFor(pixels, cap));

    [Fact]
    public void HeightFor_allows_ten_percent_upscaling()
    {
        Assert.Equal(360, VideoFormats.HeightFor(400 * 0.9));
        Assert.Equal(360, VideoFormats.HeightFor(399));
        Assert.Equal(480, VideoFormats.HeightFor(401));
    }

    [Fact]
    public void Pick_takes_the_sharpest_that_fits_preferring_30_fps()
    {
        var formats = YtDlpOutput.ParseVideoFormats("dQw4w9WgXcQ", Info(), Now);

        Assert.Equal(247, Itag(VideoFormats.Pick(formats, 720)));
        Assert.Equal(248, Itag(VideoFormats.Pick(formats, 1080)));
        Assert.Equal(243, Itag(VideoFormats.Pick(formats, 480)));
        Assert.Equal(278, Itag(VideoFormats.Pick(formats, 100))); // nothing fits: the smallest
        Assert.Null(VideoFormats.Pick([], 720));
    }

    [Fact]
    public void Pick_counts_the_short_side_of_upright_videos()
    {
        ResolvedStream Upright(int width, int height, int itag) => new()
        {
            VideoId = "short",
            Url = new Uri(Url(itag)),
            Width = width,
            Height = height,
        };

        var picked = VideoFormats.Pick([Upright(360, 640, 243), Upright(720, 1280, 247), Upright(1080, 1920, 248)], 720);

        Assert.Equal(247, Itag(picked));
    }

    private static int Itag(ResolvedStream? stream) =>
        int.Parse(System.Web.HttpUtility.ParseQueryString(stream!.Url.Query)["itag"]!, System.Globalization.CultureInfo.InvariantCulture);

    private static string Url(int itag) => $"https://rr1---sn-test.googlevideo.com/videoplayback?expire=1791000000&itag={itag}&sig=TEST";

    private static JsonObject Format(
        string id, string ext, string vcodec, string acodec, int? width, int? height, double? fps = null, double? tbr = null, string protocol = "https", string dynamicRange = "SDR")
    {
        var format = new JsonObject
        {
            ["format_id"] = id,
            ["ext"] = ext,
            ["vcodec"] = vcodec,
            ["acodec"] = acodec,
            ["protocol"] = protocol,
            ["url"] = int.TryParse(id, out var itag) ? Url(itag) : "https://i.ytimg.com/sb/dQw4w9WgXcQ/storyboard3_L0/default.jpg",
            ["http_headers"] = new JsonObject { ["User-Agent"] = "Mozilla/5.0" },
        };
        if (width is not null)
        {
            format["width"] = width;
            format["height"] = height;
            format["dynamic_range"] = dynamicRange;
        }

        if (fps is not null)
        {
            format["fps"] = fps;
        }

        if (tbr is not null)
        {
            format["tbr"] = tbr;
        }

        return format;
    }
}
