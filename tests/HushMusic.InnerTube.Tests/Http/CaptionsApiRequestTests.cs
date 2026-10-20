using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Lyrics;
using HushMusic.InnerTube.Api;
using HushMusic.InnerTube.Parsing;
using Xunit;

namespace HushMusic.InnerTube.Tests.Http;

/// <summary>Caption lyrics: the anonymous player request, the json3 GET, and reuse of the loudness response.</summary>
public sealed class CaptionsApiRequestTests : IDisposable
{
    private const string VideoId = "dQw4w9WgXcQ";

    private readonly InnerTubeTestHost _host = new();
    private readonly CaptionTrackCache _tracks;
    private readonly CaptionsApi _captions;

    public CaptionsApiRequestTests()
    {
        _tracks = new CaptionTrackCache(_host.Time);
        _captions = new CaptionsApi(_host.Client, _tracks, _host.Time, NullLogger<CaptionsApi>.Instance);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _host.Dispose();

    private static string Fixture(string name) => Fixtures.Read(Path.Combine("Captions", name));

    [Fact]
    public async Task The_player_is_asked_anonymously_then_the_track_is_fetched_as_json3()
    {
        _host.SignIn();
        _host.Handler.EnqueueJson(Fixture("player_captions_asr.json"));
        _host.Handler.EnqueueJson(Fixture("timedtext_sample_en.json"));

        var captions = await _captions.GetCaptionLyricsAsync(VideoId, Ct);

        Assert.Equal(2, _host.Handler.Requests.Count);
        var player = _host.Handler.Requests[0];
        Assert.Equal("/youtubei/v1/player", player.Uri.AbsolutePath);
        Assert.Equal(
            """{"playbackContext":{"contentPlaybackContext":{"signatureTimestamp":20732}},"video_id":"dQw4w9WgXcQ"}""",
            player.BodyWithoutContext);
        Assert.Null(player.Header("Authorization"));

        var timedText = _host.Handler.Requests[1];
        Assert.Equal(HttpMethod.Get, timedText.Method);
        Assert.Equal("www.youtube.com", timedText.Uri.Host);
        Assert.Equal("/api/timedtext", timedText.Uri.AbsolutePath);
        Assert.EndsWith("&lang=en&fmt=json3", timedText.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("kind=asr", timedText.Uri.Query, StringComparison.Ordinal);
        Assert.Equal("SOCS=CAI", timedText.Header("Cookie"));
        Assert.Null(timedText.Header("Authorization"));
        Assert.Equal(0, _host.Auth.ApplyCount);

        Assert.NotNull(captions);
        Assert.Equal("en", captions.LanguageCode);
        Assert.True(captions.LanguageConfirmed);
        Assert.Equal("English", captions.TrackName);
        Assert.Equal(10, captions.Lines.Count);
    }

    [Fact]
    public async Task The_loudness_player_response_is_reused()
    {
        var watch = new WatchApi(_host.Client, _host.Time, NullLogger<WatchApi>.Instance, _tracks);
        _host.Handler.EnqueueJson(Fixture("player_captions_asr.json"));
        _host.Handler.EnqueueJson(Fixture("timedtext_sample_en.json"));

        await watch.GetLoudnessDbAsync(VideoId, Ct);
        var captions = await _captions.GetCaptionLyricsAsync(VideoId, Ct);

        Assert.NotNull(captions);
        Assert.Equal(2, _host.Handler.Requests.Count);
        Assert.Equal("/api/timedtext", _host.Handler.Last.Uri.AbsolutePath);
    }

    [Fact]
    public async Task No_usable_track_means_no_caption_request()
    {
        _host.Handler.EnqueueJson(Fixture("player_captions_asr_only.json"));

        Assert.Null(await _captions.GetCaptionLyricsAsync("9bZkp7q19f0", Ct));
        Assert.Single(_host.Handler.Requests);
    }

    [Fact]
    public async Task An_empty_caption_body_means_no_captions()
    {
        _host.Handler.EnqueueJson(Fixture("player_captions_single.json"));
        _host.Handler.Enqueue(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(string.Empty) });

        Assert.Null(await _captions.GetCaptionLyricsAsync("4NRXx6U8ABQ", Ct));
    }

    [Fact]
    public async Task An_expired_cached_track_url_is_refreshed_once()
    {
        _tracks.Store(VideoId, CaptionsParser.ParseTracks(JsonNode.Parse(Fixture("player_captions_asr.json"))!));
        _host.Handler.EnqueueStatus(HttpStatusCode.Forbidden);
        _host.Handler.EnqueueJson(Fixture("player_captions_asr.json"));
        _host.Handler.EnqueueJson(Fixture("timedtext_sample_en.json"));

        var captions = await _captions.GetCaptionLyricsAsync(VideoId, Ct);

        Assert.NotNull(captions);
        Assert.Equal(["/api/timedtext", "/youtubei/v1/player", "/api/timedtext"], _host.Handler.Requests.Select(r => r.Uri.AbsolutePath));
    }

    [Fact]
    public async Task Only_youtube_hosts_are_fetched()
    {
        await Assert.ThrowsAsync<InnerTubeException>(() =>
            _host.Client.GetPublicJsonAsync(new Uri("https://example.com/api/timedtext?v=x"), "api/timedtext", Ct));
        Assert.Empty(_host.Handler.Requests);
    }

    [Fact]
    public void The_captions_api_is_registered()
    {
        using var paths = new TempAppPaths();
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IAppPaths>(paths)
            .AddSingleton<ISettingsService, FakeSettingsService>()
            .AddSingleton<IRequestAuthenticator, FakeAuthenticator>()
            .AddHushInnerTube()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Assert.IsType<CaptionsApi>(provider.GetRequiredService<ICaptionLyricsApi>());
        Assert.Same(provider.GetRequiredService<CaptionTrackCache>(), provider.GetRequiredService<CaptionTrackCache>());
    }
}
