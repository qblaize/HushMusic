using System.IO.Pipes;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Discord;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>
/// The Discord status against a fake Discord: a named-pipe server with a unique name in this process. The real
/// "discord-ipc-N" pipes are never touched.
/// </summary>
public sealed class DiscordPresenceTests : IAsyncDisposable
{
    private const string AppId = "123456789012345678";

    private static readonly Track Song = new()
    {
        Title = "Digital Love",
        VideoId = "song1",
        Artists = [new ArtistRef("Daft Punk", "UC1")],
        Album = new AlbumRef("Discovery", "MPREb_1"),
        Thumbnails = [new Thumbnail("https://lh3.googleusercontent.com/cover=w544-h544-l90-rj", 544, 544)],
    };

    private readonly FakeDiscord _discord = new();
    private readonly FakePlayer _player = new() { CurrentTrack = Song, Status = PlaybackStatus.Playing, Position = TimeSpan.FromSeconds(10), Duration = TimeSpan.FromSeconds(300) };
    private readonly DiscordFakeRadio _radio = new();
    private readonly DiscordFakeSettings _settings = new();
    private readonly ManualTimeProvider _time = new();
    private DiscordPresenceService? _service;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        await _discord.DisposeAsync();
    }

    [Fact]
    public async Task Off_nothing_runs_and_no_pipe_is_opened()
    {
        _discord.Listen();
        var service = Start(on: false);

        await Task.Delay(300, Ct);

        Assert.True(service.Worker.IsCompleted);
        Assert.False(_discord.IsConnected);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task Without_a_valid_application_id_nothing_runs()
    {
        _discord.Listen();
        var service = Start(on: true, applicationId: "DISCORD_APPLICATION_ID");

        await Task.Delay(300, Ct);

        Assert.True(service.Worker.IsCompleted);
        Assert.False(_discord.IsConnected);
    }

    [Fact]
    public async Task A_test_instance_never_reaches_the_real_discord()
    {
        var options = DiscordPresenceOptions.From(name => name switch
        {
            "HUSHMUSIC_TEST_BACKGROUND" => "1",
            _ => null,
        });
        var redirected = DiscordPresenceOptions.From(name => name switch
        {
            "HUSHMUSIC_TEST_BACKGROUND" => "1",
            "HUSHMUSIC_TEST_DISCORD_PIPE" => "hush-fake",
            "HUSHMUSIC_TEST_DISCORD_APP_ID" => AppId,
            _ => null,
        });
        var sneaky = DiscordPresenceOptions.From(name => name switch
        {
            "HUSHMUSIC_TEST_BACKGROUND" => "1",
            "HUSHMUSIC_TEST_DISCORD_PIPE" => "discord-ipc",
            _ => null,
        });
        var normal = DiscordPresenceOptions.From(_ => null);

        Assert.False(options.Allowed);
        Assert.Equal(("hush-fake", AppId, true), (redirected.PipeName, redirected.ApplicationId, redirected.Allowed));
        Assert.False(sneaky.Allowed);
        Assert.Equal((DiscordPresenceOptions.DefaultPipeName, DiscordPresence.ApplicationId, true), (normal.PipeName, normal.ApplicationId, normal.Allowed));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Playing_a_song_sets_a_listening_status_after_the_handshake()
    {
        _discord.Listen();
        var service = Start(on: true);

        var hello = await _discord.AcceptAsync(Ct);
        var activity = await _discord.ReadActivityAsync(Ct);

        Assert.Equal($$"""{"v":1,"client_id":"{{AppId}}"}""", hello);
        Assert.NotNull(activity);
        Assert.Equal(2, (int?)activity["type"]);
        Assert.Equal(1, (int?)activity["status_display_type"]);
        Assert.Equal("Digital Love", (string?)activity["details"]);
        Assert.Equal("Daft Punk", (string?)activity["state"]);
        Assert.Equal("Discovery", (string?)activity["assets"]!["large_text"]);
        Assert.Equal("https://lh3.googleusercontent.com/cover=w512-h512-l90-rj", (string?)activity["assets"]!["large_image"]);
        var start = _time.GetUtcNow().AddSeconds(-10).ToUnixTimeMilliseconds();
        Assert.Equal(start, (long?)activity["timestamps"]!["start"]);
        Assert.Equal(start + 300_000, (long?)activity["timestamps"]!["end"]);
        Assert.Equal(Environment.ProcessId, _discord.LastPid);
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task A_long_pause_clears_the_status_and_playing_again_restores_it()
    {
        _discord.Listen();
        Start(on: true);
        await _discord.AcceptAsync(Ct);
        var first = await _discord.ReadActivityAsync(Ct);

        // A short pause keeps the status; playing on moves the bar by the time paused.
        _player.RaiseStatusChanged(PlaybackStatus.Paused);
        _time.Advance(TimeSpan.FromSeconds(5));
        _player.RaiseStatusChanged(PlaybackStatus.Playing);
        var resumed = await _discord.ReadActivityAsync(Ct, Tick);
        Assert.Equal((long?)first!["timestamps"]!["start"] + 5_000, (long?)resumed!["timestamps"]!["start"]);

        _player.RaiseStatusChanged(PlaybackStatus.Paused);
        _time.Advance(DiscordPresenceService.PauseGrace - TimeSpan.FromSeconds(1));
        Assert.True(_discord.IsConnected);
        _time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(await _discord.ReadActivityAsync(Ct, Tick));

        _player.RaiseStatusChanged(PlaybackStatus.Playing);
        var back = await _discord.ReadActivityAsync(Ct, Tick);
        Assert.Equal("Digital Love", (string?)back?["details"]);
    }

    [Fact]
    public async Task A_seek_moves_the_progress_bar_and_small_drift_does_not()
    {
        _discord.Listen();
        Start(on: true);
        await _discord.AcceptAsync(Ct);
        var first = await _discord.ReadActivityAsync(Ct);

        _time.Advance(TimeSpan.FromSeconds(1));
        _player.RaisePositionChanged(TimeSpan.FromSeconds(11.5)); // within tolerance: nothing to send
        _player.RaisePositionChanged(TimeSpan.FromSeconds(120)); // a seek
        var moved = await _discord.ReadActivityAsync(Ct, Tick);

        var expected = (long?)first!["timestamps"]!["start"] - 110_000 + 1_000;
        Assert.Equal(expected, (long?)moved!["timestamps"]!["start"]);
        Assert.Equal(1, _discord.ActivitiesAfterFirst);
    }

    [Fact]
    public async Task Fast_skipping_is_coalesced_into_the_latest_song()
    {
        _discord.Listen();
        Start(on: true);
        await _discord.AcceptAsync(Ct);
        await _discord.ReadActivityAsync(Ct);

        foreach (var i in Enumerable.Range(1, 6))
        {
            _player.CurrentTrack = Song with { VideoId = "skip" + i, Title = "Skipped " + i };
            _player.Position = TimeSpan.Zero;
            _player.RaiseTrackChanged(_player.CurrentTrack);
        }

        var latest = await _discord.ReadActivityAsync(Ct, Tick);
        Assert.Equal("Skipped 6", (string?)latest?["details"]);
        Assert.Equal(1, _discord.ActivitiesAfterFirst);
    }

    [Fact]
    public async Task A_station_shows_the_song_on_air_with_a_remembered_match_cover()
    {
        var station = new RadioStation { Id = "st", Name = "Radio Nova", StreamUrl = "https://nova/stream", LogoUrl = "https://nova/logo.png" };
        var onAir = new RadioNowPlaying("st", "Air - Sexy Boy", "Air", "Sexy Boy", null);
        var matcher = new DiscordFakeMatcher(new Track
        {
            Title = "Sexy Boy",
            VideoId = "air1",
            Artists = [new ArtistRef("Air", "UCair")],
            Thumbnails = [new Thumbnail("https://lh3.googleusercontent.com/air=w120-h120-l90-rj", 120, 120)],
        });
        _player.CurrentTrack = station.ToTrack();
        _player.Duration = TimeSpan.Zero;
        _radio.Current = onAir;
        _discord.Listen();
        Start(on: true, matcher: matcher);
        await _discord.AcceptAsync(Ct);

        var activity = await _discord.ReadActivityAsync(Ct);

        Assert.Equal("Air - Sexy Boy", (string?)activity!["details"]);
        Assert.Equal("Radio Nova", (string?)activity["state"]);
        Assert.Null(activity["timestamps"]!["end"]);
        Assert.Equal("https://lh3.googleusercontent.com/air=w512-h512-l90-rj", (string?)activity["assets"]!["large_image"]);
    }

    [Fact]
    public async Task Pings_are_answered_with_the_same_payload()
    {
        _discord.Listen();
        Start(on: true);
        await _discord.AcceptAsync(Ct);
        await _discord.ReadActivityAsync(Ct);

        await _discord.SendAsync(DiscordOpcode.Ping, """{"ping":42}""", Ct);
        var pong = await _discord.ReadFrameAsync(Ct);

        Assert.Equal(new DiscordFrame(DiscordOpcode.Pong, """{"ping":42}"""), pong);
    }

    [Fact]
    public async Task After_discord_restarts_the_status_comes_back()
    {
        _discord.Listen();
        var service = Start(on: true);
        await _discord.AcceptAsync(Ct);
        await _discord.ReadActivityAsync(Ct);

        await _discord.SendAsync(DiscordOpcode.Close, """{"code":1000,"message":"bye"}""", Ct);
        await _discord.HangUpAsync();
        await WaitUntilAsync(() => !service.IsConnected);

        _discord.Listen();
        var hello = await _discord.AcceptAsync(Ct, Tick);
        var activity = await _discord.ReadActivityAsync(Ct, Tick);

        Assert.Contains(AppId, hello, StringComparison.Ordinal);
        Assert.Equal("Digital Love", (string?)activity?["details"]);
        Assert.True(service.IsConnected);
    }

    [Fact]
    public async Task Turning_it_off_clears_the_status_and_hangs_up()
    {
        _discord.Listen();
        var service = Start(on: true);
        await _discord.AcceptAsync(Ct);
        await _discord.ReadActivityAsync(Ct);

        await _settings.UpdateAsync(s => s.ShowOnDiscord = false, Ct);

        Assert.Null(await _discord.ReadActivityAsync(Ct));
        Assert.Null(await _discord.ReadFrameAsync(Ct)); // the pipe closed
        await service.Worker.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.False(service.IsConnected);
    }

    [Fact]
    public async Task Nothing_playing_means_no_connection_attempt()
    {
        _player.Status = PlaybackStatus.Paused; // a restored session waits paused
        _discord.Listen();
        Start(on: true);

        await Task.Delay(300, Ct);

        Assert.False(_discord.IsConnected);
    }

    [Fact]
    public async Task An_unknown_application_id_is_refused_at_the_handshake()
    {
        _discord.Listen();
        var connecting = Task.Run(() => DiscordIpcConnectionProbe.ConnectAsync(_discord.PipeName, AppId, Ct), Ct);
        await _discord.AcceptAsync(Ct, reply: """{"code":4000,"message":"Invalid Client ID"}""", replyOpcode: DiscordOpcode.Close);

        var error = await Assert.ThrowsAnyAsync<Exception>(() => connecting);

        Assert.Contains("4000", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_discord_connecting_fails_fast()
    {
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<Exception>(() => DiscordIpcConnectionProbe.ConnectAsync("hush-nobody-" + Guid.NewGuid().ToString("N"), AppId, Ct));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(2));
    }

    private DiscordPresenceService Start(bool on, string applicationId = AppId, IRadioTrackMatcher? matcher = null)
    {
        _settings.Current.ShowOnDiscord = on;
        _service = new DiscordPresenceService(
            _player,
            _radio,
            _settings,
            _time,
            NullLogger<DiscordPresenceService>.Instance,
            new DiscordPresenceOptions { PipeName = _discord.PipeName, ApplicationId = applicationId },
            matcher);
        _service.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
        return _service;
    }

    // The clock only moves when told; a read that waits on a timer moves it a second at a time.
    private void Tick() => _time.Advance(TimeSpan.FromSeconds(1));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition());
    }
}

/// <summary>Opens a connection the way the service does, for the handshake tests.</summary>
internal static class DiscordIpcConnectionProbe
{
    public static async Task ConnectAsync(string pipeName, string applicationId, CancellationToken cancellationToken)
    {
        var connection = await DiscordIpcConnection.ConnectAsync(pipeName, applicationId, NullLogger.Instance, cancellationToken);
        await connection.DisposeAsync();
    }
}

/// <summary>A pretend Discord desktop app: one pipe instance at a time, named "{PipeName}-0".</summary>
internal sealed class FakeDiscord : IAsyncDisposable
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);

    private NamedPipeServerStream? _pipe;
    private Task<DiscordFrame?>? _pendingRead;
    private int _activities;

    public string PipeName { get; } = "hush-test-" + Guid.NewGuid().ToString("N");

    public bool IsConnected => _pipe?.IsConnected == true;

    public int? LastPid { get; private set; }

    /// <summary>SET_ACTIVITY frames read since the first one.</summary>
    public int ActivitiesAfterFirst => Math.Max(0, _activities - 1);

    public void Listen() =>
        _pipe = new NamedPipeServerStream(PipeName + "-0", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

    /// <summary>Waits for the client, reads its handshake and answers READY (or <paramref name="reply"/>). Returns the handshake JSON.</summary>
    public async Task<string> AcceptAsync(CancellationToken cancellationToken, Action? tick = null, string? reply = null, DiscordOpcode replyOpcode = DiscordOpcode.Frame)
    {
        var waiting = _pipe!.WaitForConnectionAsync(cancellationToken);
        await Until(waiting, tick, cancellationToken);
        var hello = await ReadFrameAsync(cancellationToken, tick);
        Assert.Equal(DiscordOpcode.Handshake, hello!.Opcode);
        _activities = 0;
        await SendAsync(
            replyOpcode,
            reply ?? """{"cmd":"DISPATCH","data":{"v":1,"config":{"cdn_host":"cdn.discordapp.com","api_endpoint":"//discord.com/api","environment":"production"},"user":{"id":"1","username":"tester"}},"evt":"READY","nonce":null}""",
            cancellationToken);
        return hello.Payload;
    }

    /// <summary>Reads frames until a SET_ACTIVITY and answers it like Discord. Returns its activity (null = cleared).</summary>
    public async Task<JsonObject?> ReadActivityAsync(CancellationToken cancellationToken, Action? tick = null)
    {
        while (true)
        {
            var frame = await ReadFrameAsync(cancellationToken, tick) ?? throw new EndOfStreamException("The client hung up.");
            if (frame.Opcode != DiscordOpcode.Frame || JsonNode.Parse(frame.Payload) is not JsonObject json || (string?)json["cmd"] != "SET_ACTIVITY")
            {
                continue;
            }

            _activities++;
            LastPid = (int?)json["args"]!["pid"];
            try
            {
                await SendAsync(DiscordOpcode.Frame, $$"""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"{{(string?)json["nonce"]}}"}""", cancellationToken);
            }
            catch (IOException)
            {
                // The client hung up right after (turning the status off): nobody to answer.
            }

            return json["args"]!["activity"] as JsonObject;
        }
    }

    public async Task<DiscordFrame?> ReadFrameAsync(CancellationToken cancellationToken, Action? tick = null)
    {
        _pendingRead ??= DiscordIpcCodec.ReadAsync(_pipe!, cancellationToken);
        await Until(_pendingRead, tick, cancellationToken);
        var frame = await _pendingRead;
        _pendingRead = null;
        return frame;
    }

    public async Task SendAsync(DiscordOpcode opcode, string payload, CancellationToken cancellationToken)
    {
        await _pipe!.WriteAsync(DiscordIpcCodec.Encode(opcode, payload), cancellationToken);
        await _pipe.FlushAsync(cancellationToken);
    }

    public async Task HangUpAsync()
    {
        _pendingRead = null;
        if (_pipe is not null)
        {
            await _pipe.DisposeAsync();
            _pipe = null;
        }
    }

    public async ValueTask DisposeAsync() => await HangUpAsync();

    // Waits for the task, moving the test clock while it doesn't finish (the client may be waiting on a timer).
    private static async Task Until(Task task, Action? tick, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + ReadTimeout;
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("The fake Discord waited too long.");
            }

            await Task.WhenAny(task, Task.Delay(40, cancellationToken));
            if (!task.IsCompleted)
            {
                tick?.Invoke();
            }
        }
    }
}

internal sealed class DiscordFakeSettings : ISettingsService
{
    public AppSettings Current { get; } = new();

    public event EventHandler? Changed;

    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task UpdateAsync(Action<AppSettings> update, CancellationToken cancellationToken = default)
    {
        update(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

internal sealed class DiscordFakeRadio : IRadioNowPlaying
{
    public RadioNowPlaying? Current { get; set; }

    public event EventHandler<RadioNowPlayingChangedEventArgs>? Changed;

    public void Follow(RadioStation station)
    {
    }

    public void Unfollow(bool forget)
    {
    }

    public void Raise(RadioNowPlaying? song)
    {
        Current = song;
        Changed?.Invoke(this, new RadioNowPlayingChangedEventArgs(song?.StationId ?? string.Empty, song));
    }
}

internal sealed class DiscordFakeMatcher(Track match) : IRadioTrackMatcher
{
    public string GetQuery(RadioNowPlaying song, string? stationName = null) => song.StreamTitle;

    public Task<IReadOnlyList<Track>> FindAsync(RadioNowPlaying song, string? stationName = null, int count = 3, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The Discord status must never search.");

    public IReadOnlyList<Track>? Remembered(RadioNowPlaying song, string? stationName = null) => [match];
}
