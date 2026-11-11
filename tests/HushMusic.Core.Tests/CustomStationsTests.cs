using HushMusic.Core.Radio;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class CustomStationsTests
{
    private static readonly Uri Base = new("http://radio.example.com/listen/station.pls");

    [Theory]
    [InlineData("https://stream.example.com/live.mp3", "https://stream.example.com/live.mp3")]
    [InlineData("  stream.example.com:8000/live  ", "http://stream.example.com:8000/live")]
    [InlineData("http://10.0.0.5:8080/", "http://10.0.0.5:8080/")]
    public void Links_are_read_and_get_a_scheme_when_they_have_none(string text, string expected)
    {
        Assert.True(CustomStations.TryParseLink(text, out var link));
        Assert.Equal(expected, link.AbsoluteUri);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ftp://example.com/stream")]
    [InlineData("file:///C:/music/song.mp3")]
    [InlineData("not a link")]
    public void Anything_but_a_web_link_is_refused(string? text)
    {
        Assert.False(CustomStations.TryParseLink(text, out _));
    }

    [Theory]
    [InlineData("audio/mpeg", "http://s.example.com/live", StreamLinkKind.Audio)]
    [InlineData("application/octet-stream", "http://s.example.com/live", StreamLinkKind.Audio)]
    [InlineData(null, "http://s.example.com/live", StreamLinkKind.Audio)]
    [InlineData("audio/x-scpls", "http://s.example.com/listen", StreamLinkKind.Playlist)]
    [InlineData("audio/x-mpegurl", "http://s.example.com/listen", StreamLinkKind.Playlist)]
    [InlineData("application/vnd.apple.mpegurl", "http://s.example.com/live", StreamLinkKind.Playlist)]
    [InlineData("text/plain", "http://s.example.com/listen.pls", StreamLinkKind.Playlist)]
    [InlineData("application/octet-stream", "http://s.example.com/listen.M3U", StreamLinkKind.Playlist)]
    [InlineData("text/html", "http://s.example.com/listen.pls", StreamLinkKind.WebPage)]
    [InlineData("text/html; charset=utf-8", "http://s.example.com/", StreamLinkKind.WebPage)]
    [InlineData("text/plain", "http://s.example.com/", StreamLinkKind.WebPage)]
    public void Responses_are_sorted_by_type_then_extension(string? mediaType, string link, StreamLinkKind expected)
    {
        Assert.Equal(expected, CustomStations.Classify(mediaType, new Uri(link)));
    }

    [Fact]
    public void A_pls_playlist_gives_its_first_file()
    {
        const string pls = "[playlist]\r\nNumberOfEntries=2\r\nFile1=http://s1.example.com:8000/stream\r\nTitle1=Example FM\r\nFile2=http://s2.example.com/stream\r\nVersion=2\r\n";

        Assert.Equal("http://s1.example.com:8000/stream", CustomStations.FirstStreamIn(pls, Base)?.AbsoluteUri);
    }

    [Fact]
    public void An_m3u_playlist_gives_its_first_entry_and_resolves_relative_ones()
    {
        const string m3u = "#EXTM3U\n#EXTINF:-1,Example FM\n\nlive/high.aac\nhttp://backup.example.com/live\n";

        Assert.Equal("http://radio.example.com/listen/live/high.aac", CustomStations.FirstStreamIn(m3u, Base)?.AbsoluteUri);
    }

    [Fact]
    public void Asx_and_xspf_playlists_give_their_stream()
    {
        const string asx = """<asx version="3.0"><entry><title>Example</title><ref href="http://s.example.com/asx-stream" /></entry></asx>""";
        const string xspf = """<?xml version="1.0"?><playlist><trackList><track><location>https://s.example.com/x?a=1&amp;b=2</location></track></trackList></playlist>""";

        Assert.Equal("http://s.example.com/asx-stream", CustomStations.FirstStreamIn(asx, Base)?.AbsoluteUri);
        Assert.Equal("https://s.example.com/x?a=1&b=2", CustomStations.FirstStreamIn(xspf, Base)?.AbsoluteUri);
    }

    [Fact]
    public void A_playlist_without_a_web_stream_gives_nothing()
    {
        Assert.Null(CustomStations.FirstStreamIn("#EXTM3U\nC:\\Music\\song.mp3\n", Base));
        Assert.Null(CustomStations.FirstStreamIn("<html><body>Listen live!</body></html>", Base));
        Assert.Null(CustomStations.FirstStreamIn("   ", Base));
    }

    [Fact]
    public void Hls_playlists_are_recognised()
    {
        Assert.True(CustomStations.IsHls("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=128000\nchunklist.m3u8\n"));
        Assert.False(CustomStations.IsHls("#EXTM3U\nhttp://s.example.com/live\n"));
    }

    [Theory]
    [InlineData("audio/mpeg", "MP3")]
    [InlineData("audio/aacp", "AAC+")]
    [InlineData("AUDIO/AAC", "AAC")]
    [InlineData("application/ogg", "OGG")]
    [InlineData("application/octet-stream", null)]
    public void Codecs_come_from_the_content_type(string mediaType, string? expected)
    {
        Assert.Equal(expected, CustomStations.CodecFor(mediaType));
    }

    [Theory]
    [InlineData("128", 128)]
    [InlineData("192,192", 192)]
    [InlineData(" 64 ", 64)]
    [InlineData("high", null)]
    [InlineData("0", null)]
    [InlineData(null, null)]
    public void Bitrates_are_read_from_icy_br(string? value, int? expected)
    {
        Assert.Equal(expected, CustomStations.ParseBitrate(value));
    }

    [Fact]
    public void A_station_takes_the_typed_name_first_then_the_streams_then_the_host()
    {
        var probe = new StationProbeResult(new Uri("http://s.example.com:8000/live"), "Example FM", "House, Deep House; Lounge / Chill", "MP3", 128, "https://example.com/");
        var id = Guid.NewGuid();

        var typed = CustomStations.Create("  My station ", probe, id);
        var own = CustomStations.Create(null, probe, id);
        var host = CustomStations.Create("", probe with { Name = "no name" }, id);

        Assert.Equal("My station", typed.Name);
        Assert.Equal("Example FM", own.Name);
        Assert.Equal("s.example.com", host.Name);
        Assert.Equal(CustomStations.IdPrefix + id.ToString("N"), typed.Id);
        Assert.True(CustomStations.IsCustom(typed));
        Assert.False(typed.IsInDirectory);
        Assert.Equal("http://s.example.com:8000/live", typed.StreamUrl);
        Assert.Equal(["house", "deep house", "lounge"], typed.Tags);
        Assert.Equal("MP3", typed.Codec);
        Assert.Equal(128, typed.BitrateKbps);
        Assert.Equal("https://example.com/", typed.Homepage);
    }

    [Fact]
    public async Task The_probe_follows_a_playlist_and_reads_the_streams_headers()
    {
        var handler = new RadioFakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/listen.pls" => Text("[playlist]\nFile1=http://s.example.com:8000/live\n", "audio/x-scpls"),
            "/live" => Audio("audio/aacp", ("icy-name", "Example FM"), ("icy-genre", "Lounge"), ("icy-br", "64"), ("icy-url", "https://example.com")),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound),
        });
        var probe = new StationProbe(new RadioFakeHttpClientFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<StationProbe>.Instance);

        var result = await probe.ProbeAsync(new Uri("http://example.com/listen.pls"), TestContext.Current.CancellationToken);

        Assert.Equal("http://s.example.com:8000/live", result.StreamUrl.AbsoluteUri);
        Assert.Equal("Example FM", result.Name);
        Assert.Equal("Lounge", result.Genre);
        Assert.Equal("AAC+", result.Codec);
        Assert.Equal(64, result.BitrateKbps);
        Assert.Equal("https://example.com", result.Homepage);
        Assert.All(handler.Requests, r => Assert.Equal("1", r.Headers.GetValues("Icy-MetaData").Single()));
    }

    [Fact]
    public async Task The_probe_keeps_an_hls_link_as_it_is()
    {
        var handler = new RadioFakeHandler(_ => Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=96000\nchunks.m3u8\n", "application/vnd.apple.mpegurl"));
        var probe = new StationProbe(new RadioFakeHttpClientFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<StationProbe>.Instance);

        var result = await probe.ProbeAsync(new Uri("https://example.com/live/master.m3u8"), TestContext.Current.CancellationToken);

        Assert.Equal("https://example.com/live/master.m3u8", result.StreamUrl.AbsoluteUri);
    }

    [Theory]
    [InlineData("/page", "web page")]
    [InlineData("/missing", "404")]
    [InlineData("/empty.m3u", "doesn't name a stream")]
    public async Task The_probe_explains_links_that_dont_lead_to_audio(string path, string expected)
    {
        var handler = new RadioFakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/page" => Text("<html><body>Listen live</body></html>", "text/html"),
            "/empty.m3u" => Text("#EXTM3U\n", "audio/x-mpegurl"),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.NotFound) { ReasonPhrase = "Not Found" },
        });
        var probe = new StationProbe(new RadioFakeHttpClientFactory(handler), Microsoft.Extensions.Logging.Abstractions.NullLogger<StationProbe>.Instance);

        var error = await Assert.ThrowsAsync<HushException>(() => probe.ProbeAsync(new Uri("http://example.com" + path), TestContext.Current.CancellationToken));

        Assert.Contains(expected, error.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static HttpResponseMessage Text(string body, string mediaType) => new(System.Net.HttpStatusCode.OK)
    {
        Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType),
    };

    private static HttpResponseMessage Audio(string mediaType, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[4096]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mediaType);
        foreach (var (name, value) in headers)
        {
            response.Headers.TryAddWithoutValidation(name, value);
        }

        return response;
    }

    [Fact]
    public void A_station_drops_a_website_that_isnt_a_web_link_and_trims_long_names()
    {
        var probe = new StationProbeResult(new Uri("http://s.example.com/live"), new string('x', 300), null, null, null, "javascript:alert(1)");

        var station = CustomStations.Create(null, probe, Guid.NewGuid());

        Assert.Null(station.Homepage);
        Assert.Equal(CustomStations.MaxNameLength, station.Name.Length);
        Assert.Empty(station.Tags);
    }

    [Fact]
    public void A_website_without_a_scheme_is_kept()
    {
        var probe = new StationProbeResult(new Uri("http://s.example.com/live"), "Example", null, null, null, "example.radio");

        Assert.Equal("http://example.radio/", CustomStations.Create(null, probe, Guid.NewGuid()).Homepage);
    }
}
