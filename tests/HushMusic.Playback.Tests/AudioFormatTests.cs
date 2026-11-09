using System.Text.Json.Nodes;
using HushMusic.Playback.YtDlp;
using Xunit;

namespace HushMusic.Playback.Tests;

/// <summary>
/// Which audio format plays and what comes after it, on yt-dlp's <c>-j</c> output for a real track (Fixtures/YtDlp,
/// captured anonymously and trimmed to the format fields; every URL is a placeholder).
/// </summary>
public sealed class AudioFormatTests
{
    private const string VideoId = "ruY4xcFKZ5A";
    private static readonly DateTimeOffset Now = new(2026, 11, 9, 7, 52, 12, TimeSpan.Zero);

    // yt-dlp selected 251 (WebM/Opus); the list also has M4A (139, 140), lower Opus (249, 250), itag 18 and video.
    private static JsonObject Info() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "YtDlp", "webm_selected.json")))!.AsObject();

    private static JsonObject Format(JsonObject info, string id) =>
        info["formats"]!.AsArray().OfType<JsonObject>().Single(f => (string?)f["format_id"] == id);

    // The info's top level carries the selected format's fields, as yt-dlp merges them.
    private static void Select(JsonObject info, string id)
    {
        foreach (var (key, value) in Format(info, id))
        {
            info[key] = value?.DeepClone();
        }
    }

    // Numbers set in a test become typed values; the real output is parsed from text, so the parser expects that.
    private static JsonObject Reparse(JsonObject info) => JsonNode.Parse(info.ToJsonString())!.AsObject();

    private static void Remove(JsonObject info, params string[] ids)
    {
        var formats = info["formats"]!.AsArray();
        foreach (var format in formats.OfType<JsonObject>().Where(f => ids.Contains((string?)f["format_id"])).ToList())
        {
            formats.Remove(format);
        }
    }

    private static string Itag(Core.Abstractions.ResolvedStream stream) =>
        System.Web.HttpUtility.ParseQueryString(stream.Url.Query)["itag"]!;

    [Fact]
    public void The_selected_webm_opus_plays_with_the_progressive_mp4_as_fallback()
    {
        var stream = YtDlpOutput.ParseAudio(VideoId, Info(), Now);

        Assert.Equal("251", Itag(stream));
        Assert.Equal(("webm", "opus", 102), (stream.Container, stream.Codec, stream.BitrateKbps));
        Assert.Equal(TimeSpan.FromSeconds(197), stream.Duration);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794232332), stream.ExpiresAt);
        Assert.StartsWith("Mozilla/5.0", stream.Headers["User-Agent"], StringComparison.Ordinal);

        // M4A is never a fallback: Windows ends YouTube's fragmented MP4 at once.
        var fallback = Assert.IsType<Core.Abstractions.ResolvedStream>(stream.Fallback);
        Assert.Equal("18", Itag(fallback));
        Assert.Equal(("mp4", "mp4a.40.2"), (fallback.Container, fallback.Codec));
        Assert.Null(fallback.Fallback);
    }

    [Fact]
    public void Available_at_is_read_from_the_format()
    {
        var info = Info();

        var stream = YtDlpOutput.ParseAudio(VideoId, info, Now);

        // A few seconds after the player response (epoch): googlevideo answers 403 until then.
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794210737), stream.AvailableAt);
        Assert.Equal(5.0, (stream.AvailableAt!.Value - DateTimeOffset.FromUnixTimeSeconds((long)info["epoch"]!)).TotalSeconds);
        Assert.Equal(stream.AvailableAt, stream.Fallback!.AvailableAt);
    }

    [Fact]
    public void The_visionos_clients_links_are_not_held_back()
    {
        // player_client=visionos: the same WebM/Opus audio, available from the moment of the player response.
        var info = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "YtDlp", "visionos.json")))!.AsObject();

        var stream = YtDlpOutput.ParseAudio("hpSrLjc5SMs", info, Now);

        Assert.True(YtDlpOutput.HasPlayableAudio(info));
        Assert.Equal("251", Itag(stream));
        Assert.Equal(("webm", "opus", 139), (stream.Container, stream.Codec, stream.BitrateKbps));
        Assert.Equal(info["epoch"]!.GetValue<double>(), info["available_at"]!.GetValue<double>());
        Assert.Null(stream.AvailableAt); // available from the moment of the player response: nothing to wait for
        Assert.Null(stream.Fallback); // this client lists no itag 18
        Assert.NotEmpty(YtDlpOutput.ParseVideoFormats("hpSrLjc5SMs", info, Now));
    }

    [Fact]
    public void Only_m4a_is_no_playable_audio()
    {
        var info = Info();
        Remove(info, "249", "250", "251", "18");
        Select(info, "140");

        Assert.False(YtDlpOutput.HasPlayableAudio(info));
        Assert.True(YtDlpOutput.HasPlayableAudio(Info()));
    }

    [Fact]
    public void Without_available_at_the_link_works_at_once()
    {
        var info = Info();
        info.Remove("available_at");
        foreach (var format in info["formats"]!.AsArray().OfType<JsonObject>())
        {
            format.Remove("available_at");
        }

        Assert.Null(YtDlpOutput.ParseAudio(VideoId, info, Now).AvailableAt);
    }

    [Fact]
    public void A_selected_m4a_is_passed_over_for_a_format_windows_plays()
    {
        // What "bestaudio" falls back to when a track has no WebM audio.
        var info = Info();
        Remove(info, "249", "250", "251");
        Select(info, "140");

        var stream = YtDlpOutput.ParseAudio(VideoId, info, Now);

        Assert.Equal("18", Itag(stream));
        Assert.Null(stream.Fallback);
    }

    [Fact]
    public void When_the_selection_is_not_playable_the_best_webm_wins()
    {
        var info = Info();
        Select(info, "140");

        var stream = YtDlpOutput.ParseAudio(VideoId, info, Now);

        Assert.Equal(["251", "18"], Chain(stream));
    }

    [Fact]
    public void With_nothing_windows_plays_the_selection_is_returned_as_it_is()
    {
        var info = Info();
        Remove(info, "249", "250", "251", "18");
        Select(info, "140");

        var stream = YtDlpOutput.ParseAudio(VideoId, info, Now);

        Assert.Equal("140", Itag(stream));
        Assert.Null(stream.Fallback);
    }

    [Fact]
    public void Full_range_audio_beats_youtubes_compressed_drc_version()
    {
        var info = Info();
        var drc = Format(info, "251").DeepClone().AsObject();
        drc["format_id"] = "251-drc";
        drc["format_note"] = "medium, DRC";
        drc["abr"] = 140.0;
        drc["url"] = "https://rr1---sn-aigzrn7e.googlevideo.com/videoplayback?expire=1794232332&itag=251&drc=1";
        info["formats"]!.AsArray().Add(drc);
        Select(info, "140");
        info = Reparse(info);

        var plan = AudioFormats.Plan(info, info["formats"]!.AsArray().OfType<JsonObject>());

        Assert.Equal(["251", "18"], plan.Select(f => (string)f["format_id"]!));
    }

    [Fact]
    public void The_original_language_beats_a_dubbed_track()
    {
        var info = Info();
        var dub = Format(info, "251").DeepClone().AsObject();
        dub["format_id"] = "251-1";
        dub["abr"] = 160.0;
        dub["language"] = "de";
        dub["language_preference"] = -1;
        Format(info, "251")["language_preference"] = 10;
        info["formats"]!.AsArray().Add(dub);
        Select(info, "140");
        info = Reparse(info);

        var plan = AudioFormats.Plan(info, info["formats"]!.AsArray().OfType<JsonObject>());

        Assert.Equal("251", (string)plan[0]["format_id"]!);
    }

    [Theory]
    [InlineData("""{"format_id":"251","ext":"webm","acodec":"opus","vcodec":"none","protocol":"https","url":"https://example.googlevideo.com/videoplayback"}""", nameof(AudioKind.WebmOpus))]
    [InlineData("""{"format_id":"171","ext":"webm","acodec":"vorbis","vcodec":"none","protocol":"https","url":"https://example.googlevideo.com/videoplayback"}""", nameof(AudioKind.WebmOther))]
    [InlineData("""{"format_id":"18","ext":"mp4","acodec":"mp4a.40.2","vcodec":"avc1.42001E","protocol":"https","url":"https://example.googlevideo.com/videoplayback"}""", nameof(AudioKind.Progressive))]
    public void Playable_kinds(string json, string kind) =>
        Assert.Equal(Enum.Parse<AudioKind>(kind), AudioFormats.Classify(JsonNode.Parse(json)!.AsObject()));

    [Theory]
    [InlineData("""{"format_id":"140","ext":"m4a","acodec":"mp4a.40.2","vcodec":"none","protocol":"https","container":"m4a_dash","url":"https://example.googlevideo.com/videoplayback"}""")]
    [InlineData("""{"format_id":"328","ext":"m4a","acodec":"ec-3","vcodec":"none","protocol":"https","container":"m4a_dash","url":"https://example.googlevideo.com/videoplayback"}""")]
    [InlineData("""{"format_id":"251","ext":"webm","acodec":"opus","vcodec":"none","protocol":"m3u8_native","url":"https://manifest.googlevideo.com/api/manifest/hls_playlist"}""")]
    [InlineData("""{"format_id":"251","ext":"webm","acodec":"opus","vcodec":"none","protocol":"http_dash_segments","url":"https://manifest.googlevideo.com/api/manifest/dash"}""")]
    [InlineData("""{"format_id":"251","ext":"webm","acodec":"opus","vcodec":"none","protocol":"https","has_drm":true,"url":"https://example.googlevideo.com/videoplayback"}""")]
    [InlineData("""{"format_id":"251","ext":"webm","acodec":"opus","vcodec":"none","protocol":"https"}""")]
    [InlineData("""{"format_id":"247","ext":"webm","acodec":"none","vcodec":"vp9","protocol":"https","container":"webm_dash","url":"https://example.googlevideo.com/videoplayback"}""")]
    [InlineData("""{"format_id":"sb0","ext":"mhtml","acodec":"none","vcodec":"none","protocol":"mhtml","url":"https://i.ytimg.com/sb/x/storyboard3_L0/default.jpg"}""")]
    public void Formats_windows_cannot_play_over_http_are_left_out(string json) =>
        Assert.Null(AudioFormats.Classify(JsonNode.Parse(json)!.AsObject()));

    private static List<string> Chain(Core.Abstractions.ResolvedStream? stream)
    {
        var itags = new List<string>();
        for (; stream is not null; stream = stream.Fallback)
        {
            itags.Add(Itag(stream));
        }

        return itags;
    }
}
