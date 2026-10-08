using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.LastFm;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class LastFmSignatureTests
{
    [Fact]
    public void Signs_sorted_name_value_pairs_plus_secret_and_skips_format()
    {
        // md5("api_keyb25b…a026methodauth.getSessiontokenaaaa…ddddsecret"), computed with md5sum.
        var signature = LastFmSignature.Sign(
            [
                new("token", "aaaaaaaabbbbbbbbccccccccdddddddd"),
                new("method", "auth.getSession"),
                new("format", "json"),
                new("api_key", "b25b959554ed76058ac220b7b2e0a026"),
            ],
            "secret");

        Assert.Equal("b13874f3cbe0de937cdfca6ec4ff3d83", signature);
    }

    [Fact]
    public void Hashes_utf8_and_ignores_callback()
    {
        // md5("api_keyKEYartistBeyoncémethodtrack.updateNowPlayingskSESSIONtrackHaloS3CR3T") in UTF-8.
        var signature = LastFmSignature.Sign(
            [
                new("method", "track.updateNowPlaying"),
                new("track", "Halo"),
                new("artist", "Beyoncé"),
                new("sk", "SESSION"),
                new("api_key", "KEY"),
                new("callback", "cb"),
            ],
            "S3CR3T");

        Assert.Equal("4efef8a913ea2dcedddd606c7d13348d", signature);
    }
}

public sealed class ScrobbleRulesTests
{
    [Theory]
    [InlineData(180, 90)]
    [InlineData(600, 240)]
    [InlineData(480, 240)]
    [InlineData(31, 15.5)]
    public void Threshold_is_half_the_track_or_four_minutes(double trackSeconds, double thresholdSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(thresholdSeconds), ScrobbleRules.ListenThreshold(TimeSpan.FromSeconds(trackSeconds)));

    [Fact]
    public void Unknown_length_uses_four_minutes_and_is_allowed()
    {
        Assert.Equal(TimeSpan.FromMinutes(4), ScrobbleRules.ListenThreshold(null));
        Assert.True(ScrobbleRules.IsLongEnough(null));
    }

    [Fact]
    public void Tracks_of_thirty_seconds_or_less_never_count()
    {
        Assert.False(ScrobbleRules.IsLongEnough(TimeSpan.FromSeconds(30)));
        Assert.False(ScrobbleRules.IsLongEnough(TimeSpan.FromSeconds(12)));
        Assert.True(ScrobbleRules.IsLongEnough(TimeSpan.FromSeconds(31)));
    }

    [Fact]
    public void Scrobble_uses_first_artist_album_and_start_time()
    {
        var track = LastFmTestData.Track("v1", 200) with
        {
            Artists = [new ArtistRef("Main", "UC1"), new ArtistRef("Feat", "UC2")],
        };
        var started = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

        var scrobble = ScrobbleRules.ToScrobble(track, started, track.Duration);

        Assert.Equal(new LastFmScrobble("Main", "Song v1", "Album v1", started.ToUnixTimeSeconds(), 200), scrobble);
    }

    [Fact]
    public void Episodes_and_tracks_without_artist_are_skipped()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.Null(ScrobbleRules.ToScrobble(LastFmTestData.Track("e", 900) with { Type = TrackType.Episode }, now, null));
        Assert.Null(ScrobbleRules.ToScrobble(LastFmTestData.Track("a", 200) with { Artists = [] }, now, null));
    }

    [Fact]
    public void Listen_session_counts_only_playing_time()
    {
        var time = new LastFmManualTime();
        var session = new ListenSession(LastFmTestData.Track("v1", 180), time, playing: true);

        time.Advance(TimeSpan.FromSeconds(60));
        session.SetPlaying(false);
        time.Advance(TimeSpan.FromMinutes(10)); // paused: doesn't count
        session.SetPlaying(true);
        time.Advance(TimeSpan.FromSeconds(29));

        Assert.Equal(TimeSpan.FromSeconds(89), session.Listened);
        Assert.Null(session.TryComplete());

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.NotNull(session.TryComplete());
        Assert.Null(session.TryComplete()); // only once
    }

    [Fact]
    public void Listen_session_takes_the_length_from_the_player_when_metadata_has_none()
    {
        var time = new LastFmManualTime();
        var session = new ListenSession(LastFmTestData.Track("v1", null), time, playing: true);
        session.ReportDuration(TimeSpan.FromSeconds(100));

        time.Advance(TimeSpan.FromSeconds(50));

        Assert.Equal(100, session.TryComplete()?.DurationSeconds);
    }
}

public sealed class ScrobbleQueueTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("ytm-lastfm-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public async Task Persists_and_reloads_in_order()
    {
        var path = Path.Combine(_folder, "q.json");
        var queue = new ScrobbleQueue(path, NullLogger.Instance);
        queue.Enqueue(LastFmTestData.Scrobble(1));
        queue.Enqueue(LastFmTestData.Scrobble(2));
        await queue.SaveAsync(Ct);

        var reloaded = new ScrobbleQueue(path, NullLogger.Instance);
        await reloaded.LoadAsync(Ct);

        Assert.Equal([LastFmTestData.Scrobble(1), LastFmTestData.Scrobble(2)], reloaded.Peek(10));
    }

    [Fact]
    public async Task Empty_queue_deletes_the_file_and_corrupt_files_start_empty()
    {
        var path = Path.Combine(_folder, "q.json");
        var queue = new ScrobbleQueue(path, NullLogger.Instance);
        queue.Enqueue(LastFmTestData.Scrobble(1));
        await queue.SaveAsync(Ct);
        queue.Clear();
        await queue.SaveAsync(Ct);
        Assert.False(File.Exists(path));

        await File.WriteAllTextAsync(path, "{ not json", Ct);
        var corrupt = new ScrobbleQueue(path, NullLogger.Instance);
        await corrupt.LoadAsync(Ct);
        Assert.Equal(0, corrupt.Count);
    }

    [Fact]
    public void Drops_the_oldest_past_capacity()
    {
        var queue = new ScrobbleQueue(Path.Combine(_folder, "q.json"), NullLogger.Instance);
        for (var i = 1; i <= ScrobbleQueue.Capacity + 5; i++)
        {
            queue.Enqueue(LastFmTestData.Scrobble(i));
        }

        Assert.Equal(ScrobbleQueue.Capacity, queue.Count);
        Assert.Equal(6, queue.Peek(1)[0].Timestamp);
    }

    [Fact]
    public void Remove_takes_out_only_the_sent_entries()
    {
        var queue = new ScrobbleQueue(Path.Combine(_folder, "q.json"), NullLogger.Instance);
        queue.Enqueue(LastFmTestData.Scrobble(1));
        queue.Enqueue(LastFmTestData.Scrobble(2));
        var batch = queue.Peek(1);
        queue.Enqueue(LastFmTestData.Scrobble(3));

        queue.Remove(batch);

        Assert.Equal([2L, 3L], queue.Peek(10).Select(s => s.Timestamp));
    }
}

public sealed class LastFmServiceTests : IAsyncLifetime
{
    private const string ApiKey = "KEY";
    private const string Secret = "S3CR3T";

    private readonly string _folder = Directory.CreateTempSubdirectory("ytm-lastfm-").FullName;
    private readonly LastFmFakeServer _server = new();
    private readonly LastFmFakePlayer _player = new();
    private readonly LastFmFakeSettings _settings = new();
    private readonly LastFmFakeSecretStore _secrets = new();
    private readonly LastFmFakeNotifications _notifications = new();
    private readonly LastFmManualTime _time = new();
    private readonly List<LastFmService> _services = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string QueuePath => Path.Combine(_folder, "cache", "lastfm-queue.json");

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
            service.Dispose();
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public async Task Sends_now_playing_then_scrobbles_after_half_the_track()
    {
        var service = await StartConnectedAsync();
        var startedAt = _time.GetUtcNow();

        _player.Start(LastFmTestData.Track("v1", 180));
        var nowPlaying = await WaitForCallAsync("track.updateNowPlaying");
        Assert.Equal("Artist v1", nowPlaying.Form["artist"]);
        Assert.Equal("Song v1", nowPlaying.Form["track"]);
        Assert.Equal("Album v1", nowPlaying.Form["album"]);
        Assert.Equal("180", nowPlaying.Form["duration"]);
        Assert.Equal("SESSION", nowPlaying.Form["sk"]);
        Assert.Equal("json", nowPlaying.Form["format"]);
        AssertSigned(nowPlaying);

        _time.Advance(TimeSpan.FromSeconds(89));
        _player.Tick(TimeSpan.FromSeconds(89), TimeSpan.FromSeconds(180));
        Assert.Empty(_server.CallsTo("track.scrobble"));

        _time.Advance(TimeSpan.FromSeconds(1));
        _player.Tick(TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(180));
        var scrobble = await WaitForCallAsync("track.scrobble");
        Assert.Equal("Artist v1", scrobble.Form["artist[0]"]);
        Assert.Equal("Song v1", scrobble.Form["track[0]"]);
        Assert.Equal("Album v1", scrobble.Form["album[0]"]);
        Assert.Equal(startedAt.ToUnixTimeSeconds().ToString(), scrobble.Form["timestamp[0]"]);
        AssertSigned(scrobble);

        // Playing on doesn't scrobble the same play twice.
        _time.Advance(TimeSpan.FromSeconds(60));
        _player.Tick(TimeSpan.FromSeconds(150), TimeSpan.FromSeconds(180));
        await WaitUntilAsync(() => service.PendingScrobbles == 0);
        Assert.Single(_server.CallsTo("track.scrobble"));
    }

    [Fact]
    public async Task Pauses_do_not_count_towards_the_threshold()
    {
        await StartConnectedAsync();
        _player.Start(LastFmTestData.Track("v1", 600));

        _time.Advance(TimeSpan.FromMinutes(3));
        _player.Pause();
        _time.Advance(TimeSpan.FromMinutes(30));
        _player.SetStatus(PlaybackStatus.Playing);
        _time.Advance(TimeSpan.FromSeconds(59));
        _player.Tick(TimeSpan.FromSeconds(239), TimeSpan.FromSeconds(600));
        _player.ChangeTrack(null); // skipped at 3:59 of listening

        await WaitForCallAsync("track.updateNowPlaying");
        Assert.Empty(_server.CallsTo("track.scrobble"));
    }

    [Fact]
    public async Task Four_minutes_of_a_long_track_is_enough()
    {
        await StartConnectedAsync();
        _player.Start(LastFmTestData.Track("v1", 1200));

        _time.Advance(TimeSpan.FromMinutes(4));
        _player.ChangeTrack(LastFmTestData.Track("v2", 200));

        await WaitForCallAsync("track.scrobble");
    }

    [Fact]
    public async Task Short_tracks_and_disabled_scrobbling_send_nothing()
    {
        await StartConnectedAsync();
        _player.Start(LastFmTestData.Track("short", 30));
        _time.Advance(TimeSpan.FromSeconds(30));
        _player.ChangeTrack(null);

        await _settings.UpdateAsync(s => s.LastFmScrobbling = false, Ct);
        _player.Start(LastFmTestData.Track("v2", 200));
        _time.Advance(TimeSpan.FromSeconds(200));
        _player.ChangeTrack(null);

        await WaitForCallAsync("track.updateNowPlaying");
        await Task.Delay(100, Ct);
        Assert.Single(_server.Calls); // only the short track's now playing
    }

    [Fact]
    public async Task Offline_plays_are_queued_on_disk_and_sent_later()
    {
        _server.Respond = _ => throw new HttpRequestException("offline");
        var service = await StartConnectedAsync();

        _player.Start(LastFmTestData.Track("v1", 200));
        _time.Advance(TimeSpan.FromSeconds(100));
        _player.ChangeTrack(null);

        await WaitUntilAsync(() => File.Exists(QueuePath) && service.PendingScrobbles == 1 && _server.CallsTo("track.scrobble").Count == 1);
        Assert.Equal(LastFmState.Connected, service.State);

        // Next app start: the queue is loaded and flushed once Last.fm answers again.
        _server.Respond = _ => LastFmFakeServer.Json("""{"scrobbles":{"@attr":{"accepted":1,"ignored":0}}}""");
        var restarted = await StartConnectedAsync();
        await WaitUntilAsync(() => restarted.PendingScrobbles == 0 && !File.Exists(QueuePath));
        Assert.Equal("Song v1", _server.CallsTo("track.scrobble")[^1].Form["track[0]"]);
    }

    [Fact]
    public async Task Retry_timer_resends_after_a_service_outage()
    {
        var unavailable = true;
        _server.Respond = _ => unavailable
            ? LastFmFakeServer.Error(LastFmException.TemporarilyUnavailable, "Try again later", System.Net.HttpStatusCode.ServiceUnavailable)
            : LastFmFakeServer.Json("{}");
        var service = await StartConnectedAsync();

        _player.Start(LastFmTestData.Track("v1", 200));
        _time.Advance(TimeSpan.FromSeconds(100));
        _player.ChangeTrack(null);
        await WaitUntilAsync(() => _server.CallsTo("track.scrobble").Count == 1);
        Assert.Equal(1, service.PendingScrobbles);
        Assert.Equal(LastFmState.Connected, service.State);

        unavailable = false;
        await WaitUntilAsync(() =>
        {
            _time.FireTimers(); // repeated until the failed flush has released its gate
            return service.PendingScrobbles == 0;
        });
        Assert.Equal(2, _server.CallsTo("track.scrobble").Count);
    }

    [Fact]
    public async Task Sends_the_queue_in_batches_of_fifty()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(QueuePath)!);
        var pending = Enumerable.Range(1, 120).Select(LastFmTestData.Scrobble).ToArray();
        await File.WriteAllTextAsync(QueuePath, JsonSerializer.Serialize(pending, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Ct);

        var service = await StartConnectedAsync();
        await WaitUntilAsync(() => service.PendingScrobbles == 0);

        var batches = _server.CallsTo("track.scrobble");
        Assert.Equal(3, batches.Count);
        Assert.Equal([50, 50, 20], batches.Select(b => b.Form.Keys.Count(k => k.StartsWith("artist[", StringComparison.Ordinal))));
        Assert.Equal("1", batches[0].Form["timestamp[0]"]);
        Assert.Equal("50", batches[0].Form["timestamp[49]"]);
        Assert.False(batches[0].Form.ContainsKey("artist[50]"));
        Assert.Equal("120", batches[2].Form["timestamp[19]"]);
        Assert.All(batches, AssertSigned);
    }

    [Fact]
    public async Task Connect_flow_stores_credentials_and_session()
    {
        _server.Respond = call => call.Method switch
        {
            "auth.getToken" => LastFmFakeServer.Json("""{"token":"TOKEN1"}"""),
            "auth.getSession" => LastFmFakeServer.Error(LastFmException.TokenNotAuthorized, "This token has not been authorized"),
            _ => LastFmFakeServer.Json("{}"),
        };
        var service = await StartAsync();
        var changes = 0;
        service.StateChanged += (_, _) => Interlocked.Increment(ref changes);

        var url = await service.BeginConnectAsync(" KEY ", "S3CR3T", Ct);

        Assert.Equal("https://www.last.fm/api/auth/?api_key=KEY&token=TOKEN1", url.ToString());
        Assert.Equal(LastFmState.AwaitingApproval, service.State);
        Assert.Equal("KEY", _secrets.Values["lastfm.apikey"]);
        Assert.Equal("S3CR3T", _secrets.Values["lastfm.secret"]);
        AssertSigned(_server.CallsTo("auth.getToken").Single());

        Assert.False(await service.CompleteConnectAsync(Ct)); // not approved yet
        Assert.Equal(LastFmState.AwaitingApproval, service.State);

        _server.Respond = _ => LastFmFakeServer.Json("""{"session":{"name":"alice","key":"SK1","subscriber":0}}""");
        Assert.True(await service.CompleteConnectAsync(Ct));

        var getSession = _server.CallsTo("auth.getSession")[^1];
        Assert.Equal("TOKEN1", getSession.Form["token"]);
        AssertSigned(getSession);
        Assert.Equal(LastFmState.Connected, service.State);
        Assert.Equal("alice", service.UserName);
        Assert.Equal("SK1", _secrets.Values["lastfm.session"]);
        Assert.Equal("alice", _secrets.Values["lastfm.user"]);
        Assert.True(changes >= 2);
    }

    [Fact]
    public async Task Expired_token_goes_back_to_disconnected()
    {
        _server.Respond = call => call.Method == "auth.getToken"
            ? LastFmFakeServer.Json("""{"token":"T"}""")
            : LastFmFakeServer.Error(LastFmException.TokenExpired, "This token has expired");
        var service = await StartAsync();
        await service.BeginConnectAsync(ApiKey, Secret, Ct);

        var error = await Assert.ThrowsAsync<LastFmException>(() => service.CompleteConnectAsync(Ct));

        Assert.Equal(LastFmException.TokenExpired, error.Code);
        Assert.Equal(LastFmState.Disconnected, service.State);
    }

    [Fact]
    public async Task Invalid_api_key_fails_connect_without_storing_it()
    {
        _server.Respond = _ => LastFmFakeServer.Error(LastFmException.InvalidApiKey, "Invalid API key", System.Net.HttpStatusCode.Forbidden);
        var service = await StartAsync();

        var error = await Assert.ThrowsAsync<LastFmException>(() => service.BeginConnectAsync(ApiKey, Secret, Ct));

        Assert.Equal(LastFmException.InvalidApiKey, error.Code);
        Assert.Equal(LastFmState.Disconnected, service.State);
        Assert.Empty(_secrets.Values);
    }

    [Fact]
    public async Task Invalid_session_disconnects_keeps_the_queue_and_notifies()
    {
        _server.Respond = call => call.Method == "track.updateNowPlaying"
            ? LastFmFakeServer.Json("{}")
            : LastFmFakeServer.Error(LastFmException.InvalidSessionKey, "Invalid session key", System.Net.HttpStatusCode.Forbidden);
        var service = await StartConnectedAsync();

        _player.Start(LastFmTestData.Track("v1", 200));
        _time.Advance(TimeSpan.FromSeconds(100));
        _player.ChangeTrack(null);

        await WaitUntilAsync(() => service.State == LastFmState.Disconnected);
        await WaitUntilAsync(() => !_secrets.Values.ContainsKey("lastfm.session"));
        Assert.Equal(1, service.PendingScrobbles);
        var notification = Assert.Single(_notifications.Shown);
        Assert.Equal(NotificationSeverity.Warning, notification.Severity);
        Assert.Equal("Last.fm disconnected", notification.Title);
    }

    [Fact]
    public async Task Disconnect_forgets_everything()
    {
        _server.Respond = _ => throw new HttpRequestException("offline");
        var service = await StartConnectedAsync();
        _player.Start(LastFmTestData.Track("v1", 200));
        _time.Advance(TimeSpan.FromSeconds(100));
        _player.ChangeTrack(null);
        await WaitUntilAsync(() => File.Exists(QueuePath));

        await service.DisconnectAsync(Ct);

        Assert.Equal(LastFmState.Disconnected, service.State);
        Assert.Null(service.UserName);
        Assert.Empty(_secrets.Values);
        Assert.Equal(0, service.PendingScrobbles);
        Assert.False(File.Exists(QueuePath));
    }

    [Fact]
    public async Task Restores_a_stored_session_on_start()
    {
        var service = await StartConnectedAsync();

        Assert.Equal(LastFmState.Connected, service.State);
        Assert.Equal("bob", service.UserName);
    }

    private static void AssertSigned(LastFmCall call)
    {
        var unsigned = call.Form.Where(p => p.Key != "api_sig");
        Assert.Equal(LastFmSignature.Sign(unsigned, Secret), call.Form["api_sig"]);
        Assert.Equal(ApiKey, call.Form["api_key"]);
    }

    private async Task<LastFmService> StartConnectedAsync()
    {
        _secrets.Values["lastfm.apikey"] = ApiKey;
        _secrets.Values["lastfm.secret"] = Secret;
        _secrets.Values["lastfm.session"] = "SESSION";
        _secrets.Values["lastfm.user"] = "bob";
        return await StartAsync();
    }

    private async Task<LastFmService> StartAsync()
    {
        var service = new LastFmService(
            _player,
            _settings,
            _secrets,
            new AppPaths(_folder),
            _notifications,
            new LastFmFakeHttpClientFactory(_server),
            NullLogger<LastFmService>.Instance,
            _time);
        _services.Add(service);
        await service.StartAsync(Ct);
        await service.Initialization;
        return service;
    }

    private async Task<LastFmCall> WaitForCallAsync(string method)
    {
        await WaitUntilAsync(() => _server.CallsTo(method).Count > 0);
        return _server.CallsTo(method)[0];
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("Timed out waiting for the condition.");
            }

            await Task.Delay(10, Ct);
        }
    }
}

internal static class LastFmTestData
{
    public static Track Track(string videoId, double? seconds) => new()
    {
        Title = "Song " + videoId,
        VideoId = videoId,
        Artists = [new ArtistRef("Artist " + videoId, null)],
        Album = new AlbumRef("Album " + videoId, null),
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    public static LastFmScrobble Scrobble(int n) => new("Artist " + n, "Track " + n, null, n, 200);
}
