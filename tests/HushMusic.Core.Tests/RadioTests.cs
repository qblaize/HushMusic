using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features;
using HushMusic.Core.Features.LastFm;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;
using HushMusic.Core.Radio;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

internal static class RadioData
{
    public static RadioStation Station(string id = "11111111-1111-1111-1111-111111111111", string name = "Test FM", string url = "https://stream.example.com/live.mp3") => new()
    {
        Id = id,
        Name = name,
        StreamUrl = url,
        Tags = ["deep house", "lounge"],
        Country = "Romania",
        CountryCode = "RO",
        Codec = "MP3",
        BitrateKbps = 128,
    };

    /// <summary>One Radio Browser station object (only the fields the app reads).</summary>
    public static string Json(
        string uuid,
        string name,
        string url,
        string codec = "MP3",
        int lastCheckOk = 1,
        int hls = 0,
        string tags = "deep house,lounge",
        int bitrate = 128) =>
        $$"""
        {"stationuuid":"{{uuid}}","name":"{{name}}","url":"{{url}}","url_resolved":"{{url}}","homepage":"https://example.com/","favicon":"https://example.com/logo.png",
         "tags":"{{tags}}","country":"Germany","countrycode":"DE","codec":"{{codec}}","bitrate":{{bitrate}},"hls":{{hls}},"lastcheckok":{{lastCheckOk}},"clickcount":5}
        """;
}

public sealed class IcyMetadataTests
{
    private static readonly RadioStation Station = RadioData.Station(name: "Sunshine Live Chillout");

    [Fact]
    public void Reads_the_title_up_to_the_closing_quote_and_semicolon_even_with_apostrophes_inside()
    {
        const string block = "StreamTitle='Phil Terrell - I'll Erase You (Catherine Duc 'Daydream' Remix)';StreamUrl='https://somafm.com/logos/512/7soul512.jpg';";

        Assert.Equal("Phil Terrell - I'll Erase You (Catherine Duc 'Daydream' Remix)", IcyMetadata.GetField(block, "StreamTitle"));
        Assert.Equal("https://somafm.com/logos/512/7soul512.jpg", IcyMetadata.GetField(block, "StreamUrl"));
        Assert.Null(IcyMetadata.GetField(block, "StreamArtwork"));
    }

    [Fact]
    public void A_title_without_a_trailing_semicolon_ends_at_the_last_quote()
    {
        Assert.Equal("Artist - Song", IcyMetadata.GetField("StreamTitle='Artist - Song'", "StreamTitle"));
    }

    [Fact]
    public void Decodes_utf8_strips_nul_padding_and_falls_back_to_latin1()
    {
        var utf8 = Encoding.UTF8.GetBytes("StreamTitle='Beyoncé - Halo';").Concat(new byte[12]).ToArray();
        Assert.Equal("StreamTitle='Beyoncé - Halo';", IcyMetadata.Decode(utf8));

        var latin1 = Encoding.Latin1.GetBytes("StreamTitle='Sigur Rós - Hoppípolla';").Concat(new byte[5]).ToArray();
        Assert.Equal("StreamTitle='Sigur Rós - Hoppípolla';", IcyMetadata.Decode(latin1));
    }

    [Fact]
    public void Splits_artist_and_title_on_the_first_dash()
    {
        var nowPlaying = IcyMetadata.ToNowPlaying(Station, "Deepness Music - Osman Altun - Fall In To You", "https://img.example.com/cover.jpg");

        Assert.NotNull(nowPlaying);
        Assert.Equal(Station.Id, nowPlaying.StationId);
        Assert.Equal("Deepness Music", nowPlaying.Artist);
        Assert.Equal("Osman Altun - Fall In To You", nowPlaying.Title);
        Assert.Equal("https://img.example.com/cover.jpg", nowPlaying.ArtworkUrl);
    }

    [Theory]
    [InlineData("Gio Mee – Done Playing With You")]
    [InlineData("Gio Mee — Done Playing With You")]
    public void Splits_on_en_and_em_dashes_too(string streamTitle)
    {
        var nowPlaying = IcyMetadata.ToNowPlaying(Station, streamTitle);

        Assert.Equal("Gio Mee", nowPlaying?.Artist);
        Assert.Equal("Done Playing With You", nowPlaying?.Title);
    }

    [Fact]
    public void A_title_without_an_artist_is_the_whole_text_and_non_image_urls_are_ignored()
    {
        var nowPlaying = IcyMetadata.ToNowPlaying(Station, "Midnight Drive (Radio edit)", "http://www.smoothradio.com/chill");

        Assert.NotNull(nowPlaying);
        Assert.Null(nowPlaying.Artist);
        Assert.Equal("Midnight Drive (Radio edit)", nowPlaying.Title);
        Assert.Null(nowPlaying.ArtworkUrl);
    }

    [Theory]
    [InlineData("", "Any FM")]
    [InlineData("   ", "Any FM")]
    [InlineData("SUNSHINE LIVE - Chillout", "Sunshine Live Chillout")]
    [InlineData("sunshine live - Tech House", "Sunshine Live Tech House")]
    [InlineData("FluxFM - Livestream", "FluxFM")]
    [InlineData("ChillHop", "FluxFM Chillhop")]
    [InlineData("Techno Underground", "FluxFM Techno Underground")]
    [InlineData("bigFM LoFi Focus", "bigFM LoFi Focus")]
    [InlineData("bigFM Deep  Tech House", "bigFM House Beats")]
    [InlineData("Radio Swiss Jazz - www.radioswissjazz.ch", "Radio Swiss Jazz")]
    public void Titles_that_only_name_the_station_are_not_songs(string streamTitle, string stationName)
    {
        Assert.Null(IcyMetadata.ToNowPlaying(RadioData.Station(name: stationName), streamTitle));
    }

    [Theory]
    [InlineData("Luk - Deep House Live Set 2026 Luk", "1.FM Deep House Radio")]
    [InlineData("Deepness Music - Osman Altun - Fall In To You", "Deep House Radio Bucharest")]
    [InlineData("Lemon Jelly - Homage to Patagonia", "SomaFM Secret Agent")]
    [InlineData("National, The - Humiliation", "SomaFM Indie Pop Rocks!")]
    public void Real_songs_are_kept(string streamTitle, string stationName)
    {
        Assert.NotNull(IcyMetadata.ToNowPlaying(RadioData.Station(name: stationName), streamTitle));
    }

    [Fact]
    public async Task Reads_the_first_non_empty_block_from_the_stream()
    {
        const int interval = 32;
        var stream = new MemoryStream();
        stream.Write(new byte[interval]);
        stream.WriteByte(0); // empty block: title unchanged
        stream.Write(new byte[interval]);
        var meta = Encoding.UTF8.GetBytes("StreamTitle='Kooma - Portal';StreamUrl='';");
        var padded = new byte[(meta.Length + 15) / 16 * 16];
        meta.CopyTo(padded, 0);
        stream.WriteByte((byte)(padded.Length / 16));
        stream.Write(padded);
        stream.Write(new byte[interval]);
        stream.Position = 0;

        var result = await IcyMetadataReader.ReadBlocksAsync(stream, interval, TestContext.Current.CancellationToken);

        Assert.True(result.HasMetadata);
        Assert.Equal("Kooma - Portal", result.StreamTitle);
        Assert.Equal(string.Empty, result.StreamUrl);
    }

    [Fact]
    public async Task Gives_up_after_a_few_empty_blocks()
    {
        const int interval = 16;
        var stream = new MemoryStream();
        for (var i = 0; i < IcyMetadataReader.MaxBlocks + 2; i++)
        {
            stream.Write(new byte[interval]);
            stream.WriteByte(0);
        }

        stream.Position = 0;
        var result = await IcyMetadataReader.ReadBlocksAsync(stream, interval, TestContext.Current.CancellationToken);

        Assert.True(result.HasMetadata);
        Assert.Null(result.StreamTitle);
    }

    [Fact]
    public async Task A_stream_without_icy_metaint_has_no_metadata()
    {
        using var handler = new RadioFakeHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[64]) });
        using var client = new HttpClient(handler);

        var result = await IcyMetadataReader.ReadAsync(client, new Uri("https://stream.example.com/live"), TestContext.Current.CancellationToken);

        Assert.False(result.HasMetadata);
        Assert.Equal("1", handler.Requests.Single().Headers.GetValues("Icy-MetaData").Single());
    }

    [Fact]
    public async Task Reads_a_title_over_http()
    {
        var body = new MemoryStream();
        body.Write(new byte[100]);
        var meta = Encoding.UTF8.GetBytes("StreamTitle='Patchwork - Talvin';");
        var padded = new byte[48];
        meta.CopyTo(padded, 0);
        body.WriteByte(3);
        body.Write(padded);
        using var handler = new RadioFakeHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body.ToArray()) };
            response.Headers.TryAddWithoutValidation("icy-metaint", "100");
            return response;
        });
        using var client = new HttpClient(handler);

        var result = await IcyMetadataReader.ReadAsync(client, new Uri("https://stream.example.com/live"), TestContext.Current.CancellationToken);

        Assert.Equal("Patchwork - Talvin", result.StreamTitle);
    }
}

public sealed class LiveRadioTests
{
    [Fact]
    public void A_station_becomes_a_radio_track()
    {
        var station = RadioData.Station() with { LogoUrl = "https://example.com/logo.png" };

        var track = station.ToTrack();

        Assert.True(track.IsLiveRadio);
        Assert.Same(station, track.Station);
        Assert.Equal("radio:" + station.Id, track.VideoId);
        Assert.True(LiveRadio.IsRadioId(track.VideoId));
        Assert.Equal("Test FM", track.Title);
        Assert.Equal("Deep house, Lounge · Romania", track.ArtistsText);
        Assert.Equal("https://example.com/logo.png", track.ThumbnailFor(226)?.Url);
        Assert.Null(track.Duration);
    }

    [Fact]
    public void Display_text_follows_the_song_and_falls_back_to_the_station()
    {
        var track = RadioData.Station().ToTrack();
        var song = new RadioNowPlaying(track.Station!.Id, "Kooma - Portal", "Kooma", "Portal", null);
        var other = song with { StationId = "someone-else" };

        Assert.Equal("Portal", LiveRadio.DisplayTitle(track, song));
        Assert.Equal("Kooma · Test FM", LiveRadio.DisplaySubtitle(track, song));
        Assert.Equal("Test FM", LiveRadio.DisplayTitle(track, other));
        Assert.Equal("Test FM", LiveRadio.DisplayTitle(track, null));
        Assert.Equal("Deep house, Lounge · Romania", LiveRadio.DisplaySubtitle(track, null));
        Assert.Equal("Test FM", LiveRadio.DisplaySubtitle(track, song with { Artist = null }));
    }
}

public sealed class RadioBrowserParserTests
{
    [Fact]
    public void Keeps_only_playable_working_stations_without_duplicates()
    {
        var json = "[" + string.Join(",",
            RadioData.Json("a0000000-0000-0000-0000-000000000001", "Good MP3", "https://a.example.com/stream"),
            RadioData.Json("a0000000-0000-0000-0000-000000000002", "Good AAC+", "https://b.example.com/stream", codec: "AAC+"),
            RadioData.Json("a0000000-0000-0000-0000-000000000003", "Ogg", "https://c.example.com/stream", codec: "OGG"),
            RadioData.Json("a0000000-0000-0000-0000-000000000004", "Broken", "https://d.example.com/stream", lastCheckOk: 0),
            RadioData.Json("a0000000-0000-0000-0000-000000000005", "Hls", "https://e.example.com/stream", hls: 1),
            RadioData.Json("a0000000-0000-0000-0000-000000000006", "Playlist", "https://f.example.com/listen.pls"),
            RadioData.Json("a0000000-0000-0000-0000-000000000007", "good  mp3", "https://g.example.com/stream"),
            RadioData.Json("a0000000-0000-0000-0000-000000000008", "Same stream", "https://a.example.com/stream/"),
            RadioData.Json("a0000000-0000-0000-0000-000000000009", "Not http", "rtsp://h.example.com/stream")) + "]";

        var stations = RadioBrowserParser.ParseStations(json);

        Assert.Equal(["Good MP3", "Good AAC+"], stations.Select(s => s.Name));
        var first = stations[0];
        Assert.Equal("a0000000-0000-0000-0000-000000000001", first.Id);
        Assert.True(first.IsInDirectory);
        Assert.Equal("https://a.example.com/stream", first.StreamUrl);
        Assert.Equal("https://example.com/logo.png", first.LogoUrl);
        Assert.Equal(["deep house", "lounge"], first.Tags);
        Assert.Equal("DE", first.CountryCode);
        Assert.Equal(128, first.BitrateKbps);
    }

    [Fact]
    public void Interleaves_tag_results_without_repeating_a_station()
    {
        var a = RadioData.Station("1", "A", "https://x/a");
        var b = RadioData.Station("2", "B", "https://x/b");
        var c = RadioData.Station("3", "C", "https://x/c");
        var d = RadioData.Station("4", "D", "https://x/d");

        var merged = RadioBrowserParser.Interleave([[a, b, c], [d, b]], limit: 10);

        Assert.Equal(["A", "D", "B", "C"], merged.Select(s => s.Name));
        Assert.Equal(2, RadioBrowserParser.Interleave([[a, b, c], [d, b]], limit: 2).Count);
    }
}

public sealed class RadioBrowserClientTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Discovers_mirrors_queries_exact_tags_and_caches_results()
    {
        using var handler = new RadioFakeHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/json/servers" => Json("""[{"name":"de9.api.radio-browser.info"},{"name":"de9.api.radio-browser.info"}]"""),
            "/json/stations/search" => Json("[" + RadioData.Json("b0000000-0000-0000-0000-000000000001", "Deep One", "https://a.example.com/s") + "]"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var client = CreateClient(handler);

        var first = await client.GetByTagsAsync(["deep house"], 30, Ct);
        var second = await client.GetByTagsAsync(["deep house"], 30, Ct);

        Assert.Equal(["Deep One"], first.Select(s => s.Name));
        Assert.Same(first[0], second[0]);
        var search = handler.Requests.Where(r => r.RequestUri!.AbsolutePath == "/json/stations/search").ToList();
        var uri = Assert.Single(search).RequestUri!;
        Assert.Equal("de9.api.radio-browser.info", uri.Host);
        Assert.Contains("tag=deep%20house", uri.Query, StringComparison.Ordinal);
        Assert.Contains("tagExact=true", uri.Query, StringComparison.Ordinal);
        Assert.Contains("hidebroken=true", uri.Query, StringComparison.Ordinal);
        Assert.Contains("order=clickcount", uri.Query, StringComparison.Ordinal);
        Assert.All(handler.Requests, r => Assert.Contains("HushMusic/1.0", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Moves_on_to_the_next_mirror_after_a_server_error()
    {
        using var handler = new RadioFakeHandler(request => request.RequestUri!.Host switch
        {
            "all.api.radio-browser.info" when request.RequestUri.AbsolutePath == "/json/servers" => throw new HttpRequestException("no route"),
            "de1.api.radio-browser.info" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            _ => Json("[" + RadioData.Json("b0000000-0000-0000-0000-000000000002", "Found", "https://a.example.com/s") + "]"),
        });
        var client = CreateClient(handler);

        var stations = await client.SearchAsync("found", 10, Ct);

        Assert.Equal(["Found"], stations.Select(s => s.Name));
        Assert.Equal(
            ["de1.api.radio-browser.info", "de2.api.radio-browser.info"],
            handler.Requests.Where(r => r.RequestUri!.AbsolutePath == "/json/stations/search").Select(r => r.RequestUri!.Host));
    }

    [Fact]
    public async Task Fails_with_a_readable_error_when_no_mirror_answers()
    {
        using var handler = new RadioFakeHandler(_ => throw new HttpRequestException("offline"));
        var client = CreateClient(handler);

        var error = await Assert.ThrowsAsync<HushException>(() => client.SearchAsync("x", 10, Ct));

        Assert.Contains("radio directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Counts_clicks_only_for_directory_stations()
    {
        using var handler = new RadioFakeHandler(request => request.RequestUri!.AbsolutePath == "/json/servers"
            ? Json("""[{"name":"de1.api.radio-browser.info"}]""")
            : Json("""{"ok":true}"""));
        var client = CreateClient(handler);

        await client.CountClickAsync("curated-record-techno", Ct);
        await client.CountClickAsync("960cf833-0601-11e8-ae97-52543be04c81", Ct);

        var click = Assert.Single(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/json/url/", StringComparison.Ordinal));
        Assert.Equal("/json/url/960cf833-0601-11e8-ae97-52543be04c81", click.RequestUri!.AbsolutePath);
    }

    private static RadioBrowserClient CreateClient(HttpMessageHandler handler) =>
        new(new RadioFakeHttpClientFactory(handler), TimeProvider.System, NullLogger<RadioBrowserClient>.Instance);

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class RadioFavoritesStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ytm-radio-tests-" + Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Adds_newest_first_removes_and_survives_a_restart()
    {
        var paths = new AppPaths(_root);
        var store = new RadioFavoritesStore(paths, NullLogger<RadioFavoritesStore>.Instance);
        var changes = 0;
        store.Changed += (_, _) => changes++;
        await store.LoadAsync(Ct);

        var a = RadioData.Station("a", "A FM");
        var b = RadioData.Station("b", "B FM");
        await store.SetAsync(a, true, Ct);
        await store.SetAsync(b, true, Ct);
        await store.SetAsync(b, true, Ct); // already there: nothing happens
        await store.SetAsync(a, false, Ct);
        await store.SetAsync(a, true, Ct);

        Assert.Equal(["a", "b"], store.Items.Select(s => s.Id));
        Assert.True(store.Contains("b"));
        Assert.Equal(4, changes);

        var reloaded = new RadioFavoritesStore(paths, NullLogger<RadioFavoritesStore>.Instance);
        await reloaded.LoadAsync(Ct);
        Assert.Equal(["a", "b"], reloaded.Items.Select(s => s.Id));
        Assert.Equal(a, reloaded.Items[0] with { Tags = a.Tags });
        Assert.Equal(["deep house", "lounge"], reloaded.Items[0].Tags);
        Assert.False(File.Exists(Path.Combine(_root, RadioFavoritesStore.FileName + ".tmp")));
    }

    [Fact]
    public async Task An_unreadable_file_starts_empty()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, RadioFavoritesStore.FileName), "{ not json", Ct);
        var store = new RadioFavoritesStore(new AppPaths(_root), NullLogger<RadioFavoritesStore>.Instance);

        await store.LoadAsync(Ct);

        Assert.Empty(store.Items);
    }
}

public sealed class CuratedRadioTests
{
    [Fact]
    public void Every_genre_has_four_to_eight_valid_unique_stations()
    {
        var genres = CuratedRadio.Genres;

        Assert.Equal(["Deep House", "Tech House", "Smooth & Lounge", "Fresh", "Chillout", "Techno", "Lo-fi"], genres.Select(g => g.Name));
        Assert.All(genres, genre =>
        {
            Assert.InRange(genre.Stations.Count, 4, 8);
            Assert.NotEmpty(genre.DirectoryTags);
            Assert.All(genre.Stations, station =>
            {
                Assert.False(string.IsNullOrWhiteSpace(station.Name));
                Assert.True(Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https", station.StreamUrl);
                Assert.Contains(station.Codec, new[] { "MP3", "AAC", "AAC+" });
                Assert.True(station.IsInDirectory || station.Id.StartsWith("curated-", StringComparison.Ordinal), station.Id);
                Assert.True(station.LogoUrl is null || Uri.IsWellFormedUriString(station.LogoUrl, UriKind.Absolute), station.LogoUrl);
            });
        });

        var all = genres.SelectMany(g => g.Stations).ToList();
        Assert.Equal(all.Count, all.Select(s => s.Id).Distinct().Count());
        Assert.Same(all[0], CuratedRadio.Find(all[0].Id));
    }
}

public sealed class RadioStreamResolverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Stations_resolve_to_their_own_url_and_never_reach_the_inner_resolver()
    {
        var inner = new RecordingResolver();
        var resolver = new RadioStreamResolver(inner);
        var track = RadioData.Station(url: "http://live.example.com:7000/radio.mp3").ToTrack();

        var stream = await resolver.ResolveAsync(track, Ct);
        resolver.Prefetch(track.VideoId);
        resolver.Invalidate(track.VideoId);

        Assert.True(stream.IsLive);
        Assert.Equal(new Uri("http://live.example.com:7000/radio.mp3"), stream.Url);
        Assert.Equal(DateTimeOffset.MaxValue, stream.ExpiresAt);
        Assert.Empty(inner.Calls);
        await Assert.ThrowsAsync<StreamResolutionException>(() => resolver.ResolveAsync(track.VideoId, Ct));
    }

    [Fact]
    public async Task Youtube_tracks_go_to_the_inner_resolver()
    {
        var inner = new RecordingResolver();
        var resolver = new RadioStreamResolver(inner);

        await resolver.ResolveAsync(TestData.Track("abc"), Ct);
        resolver.Prefetch("def");
        resolver.Invalidate("ghi");

        Assert.Equal(["resolve abc", "prefetch def", "invalidate ghi"], inner.Calls);
    }

    private sealed class RecordingResolver : IStreamResolver
    {
        public List<string> Calls { get; } = [];

        public Task<ResolvedStream> ResolveAsync(string videoId, CancellationToken cancellationToken = default)
        {
            Calls.Add("resolve " + videoId);
            return Task.FromResult(new ResolvedStream { VideoId = videoId, Url = new Uri("https://example.com/" + videoId) });
        }

        public void Invalidate(string videoId) => Calls.Add("invalidate " + videoId);

        public void Prefetch(string videoId) => Calls.Add("prefetch " + videoId);

        public Task<string?> GetBackendVersionAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>("1");

        public Task<bool> UpdateBackendAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
    }
}

public sealed class LiveReconnectPolicyTests
{
    private readonly ManualTimeProvider _time = new();

    [Fact]
    public void Before_the_first_play_retries_once_quickly_and_never_after_a_fatal_error()
    {
        var policy = new LiveReconnectPolicy(_time);

        Assert.Equal(LiveReconnectPolicy.FirstPlayRetryDelay, policy.OnFailure(fatal: false));
        Assert.Null(policy.OnFailure(fatal: false));

        policy.Reset();
        Assert.Null(policy.OnFailure(fatal: true));
    }

    [Fact]
    public void After_a_drop_backs_off_and_gives_up_after_four_attempts()
    {
        var policy = new LiveReconnectPolicy(_time);
        policy.OnPlaying();

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8), null],
            Enumerable.Range(0, 5).Select(_ => policy.OnFailure(fatal: false)).ToArray());
    }

    [Fact]
    public void Errors_that_would_be_fatal_on_a_new_station_are_retried_after_it_has_played()
    {
        var policy = new LiveReconnectPolicy(_time);
        policy.OnPlaying();

        Assert.Equal(TimeSpan.FromSeconds(1), policy.OnFailure(fatal: true));
    }

    [Fact]
    public void A_stable_connection_earns_a_fresh_set_of_attempts()
    {
        var policy = new LiveReconnectPolicy(_time);
        policy.OnPlaying();
        policy.OnFailure(fatal: false);
        policy.OnFailure(fatal: false);
        policy.OnFailure(fatal: false);

        // Reconnected, then played long enough to count as healthy again.
        policy.OnPlaying();
        _time.Advance(LiveReconnectPolicy.StableAfter);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.OnFailure(fatal: false));

        // A connection that drops again quickly keeps counting.
        policy.OnPlaying();
        _time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(2), policy.OnFailure(fatal: false));
    }

    [Fact]
    public void Pressing_play_again_resets_the_attempts()
    {
        var policy = new LiveReconnectPolicy(_time);
        policy.OnPlaying();
        for (var i = 0; i < LiveReconnectPolicy.MaxAttemptsAfterDrop; i++)
        {
            policy.OnFailure(fatal: false);
        }

        policy.ResetAttempts();

        Assert.True(policy.HasPlayed);
        Assert.Equal(TimeSpan.FromSeconds(1), policy.OnFailure(fatal: false));
    }
}

public sealed class RadioFeatureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Radio_is_not_reported_to_youtube_history()
    {
        var player = new FakePlayer();
        var account = new FakeAccountApi();
        var reporter = new PlayHistoryReporter(player, account, new FakeAuth(), new FakeSettings(), NullLogger<PlayHistoryReporter>.Instance);
        await reporter.StartAsync(Ct);

        player.RaiseTrackStarted(RadioData.Station().ToTrack());
        player.RaiseTrackStarted(TestData.Track("a"));

        Assert.Equal(["a"], account.HistoryItems);
        await reporter.StopAsync(Ct);
    }

    [Fact]
    public void Radio_is_never_scrobbled()
    {
        Assert.Null(ScrobbleRules.ToScrobble(RadioData.Station().ToTrack(), DateTimeOffset.UnixEpoch, null));
    }

    [Fact]
    public async Task Playing_a_station_with_up_next_does_not_ask_youtube_for_a_queue()
    {
        var queue = new QueueService();
        var player = new FakePlayer();
        var watch = new FakeWatchApi();
        var actions = new PlaybackActions(queue, player, watch, new FakeBrowseApi(), new FakeNotifications(), NullLogger<PlaybackActions>.Instance);
        var track = RadioData.Station().ToTrack();

        await actions.PlayTrackWithUpNextAsync(track, Ct);
        await actions.StartRadioAsync(track, Ct);

        Assert.Empty(watch.Calls);
        Assert.Equal([track.VideoId], TestData.Ids(queue.Items));
        Assert.Equal(QueueSourceKind.LiveRadio, queue.Source?.Kind);
        Assert.Equal([0, 0], player.PlayedIndices);
    }

    [Fact]
    public async Task Volume_normalization_never_looks_up_a_station()
    {
        var watch = new FakeWatchApi();
        var normalizer = new VolumeNormalizer(watch, new FakeSettings(), NullLogger<VolumeNormalizer>.Instance);
        var id = RadioData.Station().ToTrack().VideoId;

        normalizer.Prefetch(id);
        Assert.Null(await normalizer.GetLoudnessDbAsync(id, Ct));
        Assert.True(normalizer.TryGetLoudness(id, out var loudness));
        Assert.Null(loudness);
        Assert.Empty(watch.LoudnessCalls);
    }

    [Fact]
    public async Task Click_reporter_counts_directory_stations_once_per_start()
    {
        var player = new FakePlayer();
        var directory = new FakeRadioDirectory();
        using var reporter = new RadioClickReporter(player, directory);
        await reporter.StartAsync(Ct);

        player.RaiseTrackStarted(RadioData.Station().ToTrack());
        player.RaiseTrackStarted(RadioData.Station(id: "curated-x").ToTrack());
        player.RaiseTrackStarted(TestData.Track("a"));

        Assert.Equal(["11111111-1111-1111-1111-111111111111"], directory.Clicks);
        await reporter.StopAsync(Ct);
    }

    private sealed class FakeRadioDirectory : IRadioDirectory
    {
        public List<string> Clicks { get; } = [];

        public Task<IReadOnlyList<RadioStation>> GetByTagsAsync(IReadOnlyList<string> tags, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RadioStation>>([]);

        public Task<IReadOnlyList<RadioStation>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<RadioStation>>([]);

        public Task CountClickAsync(string stationId, CancellationToken cancellationToken = default)
        {
            Clicks.Add(stationId);
            return Task.CompletedTask;
        }
    }
}

internal sealed class RadioFakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    private readonly Lock _gate = new();
    private readonly List<HttpRequestMessage> _requests = [];

    public IReadOnlyList<HttpRequestMessage> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _requests.Add(request);
        }

        return Task.FromResult(respond(request));
    }
}

internal sealed class RadioFakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        var client = new HttpClient(handler, disposeHandler: false);
        client.DefaultRequestHeaders.UserAgent.Add(RadioBrowserClient.UserAgent);
        return client;
    }
}
