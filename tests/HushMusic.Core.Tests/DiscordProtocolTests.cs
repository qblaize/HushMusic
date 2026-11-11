using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using HushMusic.Core.Features.Discord;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class DiscordFrameTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_frame_is_opcode_and_length_little_endian_then_utf8_json()
    {
        var frame = DiscordIpcCodec.Encode(DiscordOpcode.Frame, """{"t":"Café"}""");

        Assert.Equal(1, BinaryPrimitives.ReadInt32LittleEndian(frame));
        Assert.Equal(13, BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(4))); // "é" is two bytes
        Assert.Equal("""{"t":"Café"}""", Encoding.UTF8.GetString(frame.AsSpan(DiscordIpcCodec.HeaderSize)));
        Assert.Equal(new byte[] { 1, 0, 0, 0, 13, 0, 0, 0 }, frame[..8]);
    }

    [Fact]
    public async Task Frames_read_back_in_order()
    {
        using var stream = new MemoryStream([
            .. DiscordIpcCodec.Encode(DiscordOpcode.Ping, """{"n":1}"""),
            .. DiscordIpcCodec.Encode(DiscordOpcode.Close, """{"code":4000,"message":"Invalid Client ID"}"""),
            .. DiscordIpcCodec.Encode(DiscordOpcode.Pong, string.Empty),
        ]);

        Assert.Equal(new DiscordFrame(DiscordOpcode.Ping, """{"n":1}"""), await DiscordIpcCodec.ReadAsync(stream, Ct));
        var close = await DiscordIpcCodec.ReadAsync(stream, Ct);
        Assert.Equal(DiscordOpcode.Close, close!.Opcode);
        Assert.Equal((4000, "Invalid Client ID"), (DiscordRpcMessages.TryParse(close.Payload)!.Code, DiscordRpcMessages.TryParse(close.Payload)!.Message));
        Assert.Equal(new DiscordFrame(DiscordOpcode.Pong, string.Empty), await DiscordIpcCodec.ReadAsync(stream, Ct));
        Assert.Null(await DiscordIpcCodec.ReadAsync(stream, Ct)); // clean end
    }

    [Fact]
    public async Task A_stream_that_ends_inside_a_frame_is_an_error()
    {
        var frame = DiscordIpcCodec.Encode(DiscordOpcode.Frame, """{"cmd":"DISPATCH"}""");
        using var header = new MemoryStream(frame[..5]);
        using var payload = new MemoryStream(frame[..^3]);

        await Assert.ThrowsAsync<EndOfStreamException>(() => DiscordIpcCodec.ReadAsync(header, Ct));
        await Assert.ThrowsAnyAsync<EndOfStreamException>(() => DiscordIpcCodec.ReadAsync(payload, Ct));
    }

    [Theory]
    [InlineData(7, 2)]
    [InlineData(-1, 2)]
    [InlineData(1, -5)]
    [InlineData(1, DiscordIpcCodec.MaxFrameSize)]
    public async Task Unknown_opcodes_and_bad_lengths_are_refused(int opcode, int length)
    {
        var header = new byte[DiscordIpcCodec.HeaderSize + 2];
        BinaryPrimitives.WriteInt32LittleEndian(header, opcode);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), length);

        await Assert.ThrowsAsync<InvalidDataException>(() => DiscordIpcCodec.ReadAsync(new MemoryStream(header), Ct));
    }

    [Fact]
    public void A_payload_over_64_KiB_is_not_encoded()
    {
        Assert.Throws<ArgumentException>(() => DiscordIpcCodec.Encode(DiscordOpcode.Frame, new string('x', DiscordIpcCodec.MaxFrameSize)));
    }

    [Fact]
    public void Handshake_names_version_1_and_the_application()
    {
        Assert.Equal("""{"v":1,"client_id":"1234567890123456789"}""", DiscordRpcMessages.Handshake("1234567890123456789"));
    }

    [Fact]
    public void Set_activity_carries_the_pid_the_activity_and_a_nonce()
    {
        var json = JsonNode.Parse(DiscordRpcMessages.SetActivity(4242, new JsonObject { ["type"] = 2 }, "n-1"))!;

        Assert.Equal("SET_ACTIVITY", (string?)json["cmd"]);
        Assert.Equal(4242, (int?)json["args"]!["pid"]);
        Assert.Equal(2, (int?)json["args"]!["activity"]!["type"]);
        Assert.Equal("n-1", (string?)json["nonce"]);
    }

    [Fact]
    public void Clearing_sends_a_null_activity()
    {
        Assert.Equal(
            """{"cmd":"SET_ACTIVITY","args":{"pid":7,"activity":null},"nonce":"n"}""",
            DiscordRpcMessages.SetActivity(7, null, "n"));
    }

    [Fact]
    public void Ready_and_errors_are_recognised()
    {
        Assert.True(DiscordRpcMessages.IsReady("""{"cmd":"DISPATCH","data":{"v":1,"config":{}},"evt":"READY","nonce":null}"""));
        Assert.False(DiscordRpcMessages.IsReady("""{"cmd":"SET_ACTIVITY","data":{},"evt":null,"nonce":"1"}"""));
        Assert.False(DiscordRpcMessages.IsReady("not json"));

        var error = DiscordRpcMessages.TryParse("""{"cmd":"SET_ACTIVITY","data":{"code":4000,"message":"child \"activity\" fails"},"evt":"ERROR","nonce":"1"}""")!;
        Assert.True(error.IsError);
        Assert.Equal(4000, error.Code);
        Assert.Equal("1", error.Nonce);
    }
}

public sealed class DiscordActivityTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 20, 0, 0, TimeSpan.Zero);

    private static Track Song(string? album = "Discovery", string art = "https://lh3.googleusercontent.com/abc=w544-h544-l90-rj") => new()
    {
        Title = "One More Time",
        VideoId = "FGBhQbmPwH8",
        Artists = [new ArtistRef("Daft Punk", "UC1"), new ArtistRef("Romanthony", "UC2")],
        Album = album is null ? null : new AlbumRef(album, "MPREb_1"),
        Thumbnails = art.Length == 0 ? [] : [new Thumbnail(art.Replace("w544-h544", "w120-h120", StringComparison.Ordinal), 120, 120), new Thumbnail(art, 544, 544)],
    };

    [Fact]
    public void A_song_is_a_listening_activity_with_progress_and_cover()
    {
        var activity = DiscordActivity.ForSong(Song(), Start, TimeSpan.FromSeconds(320));

        Assert.Equal(
            """{"type":2,"status_display_type":1,"details":"One More Time","state":"Daft Punk, Romanthony","timestamps":{"start":1794340800000,"end":1794341120000},"assets":{"large_image":"https://lh3.googleusercontent.com/abc=w512-h512-l90-rj","large_text":"Discovery"}}""",
            activity.ToJsonString());
    }

    [Fact]
    public void Unknown_length_album_or_cover_leave_their_fields_out()
    {
        var activity = DiscordActivity.ForSong(Song(album: null, art: string.Empty), Start, null);

        Assert.Equal(1794340800000, (long?)activity["timestamps"]!["start"]);
        Assert.Null(activity["timestamps"]!["end"]);
        Assert.Null(activity["assets"]);

        var noAlbum = DiscordActivity.ForSong(Song(album: null), Start, TimeSpan.FromMinutes(3));
        Assert.NotNull(noAlbum["assets"]!["large_image"]);
        Assert.Null(noAlbum["assets"]!["large_text"]);
    }

    [Fact]
    public void A_station_shows_the_song_on_air_and_its_name_without_an_end()
    {
        var station = new RadioStation { Id = "s1", Name = "Radio Paradise", StreamUrl = "https://stream", LogoUrl = "https://logo/p.png" };
        var onAir = new RadioNowPlaying("s1", "Air - La femme d'argent", "Air", "La femme d'argent", null);

        var activity = DiscordActivity.ForStation(station, onAir, Start, station.LogoUrl);

        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"type":2,"status_display_type":1,"details":"Air - La femme d'argent","state":"Radio Paradise","timestamps":{"start":1794340800000},"assets":{"large_image":"https://logo/p.png","large_text":"Radio Paradise"}}"""),
            activity));
    }

    [Fact]
    public void A_station_without_a_known_song_shows_its_name()
    {
        var station = new RadioStation { Id = "s1", Name = "FIP", StreamUrl = "https://stream", LogoUrl = "http://insecure/logo.png" };
        var otherStation = new RadioNowPlaying("s2", "Elsewhere", null, "Elsewhere", null);

        var activity = DiscordActivity.ForStation(station, otherStation, Start, station.LogoUrl);

        Assert.Equal("FIP", (string?)activity["details"]);
        Assert.Equal("FIP", (string?)activity["state"]);
        Assert.Null(activity["assets"]); // only https images
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  A  ", "A​")]
    [InlineData("Daft   Punk\n", "Daft Punk")]
    public void Texts_are_trimmed_and_padded_to_two_characters(string? input, string? expected)
    {
        Assert.Equal(expected, DiscordActivity.Text(input));
    }

    [Fact]
    public void Long_texts_are_cut_to_128_characters_with_an_ellipsis()
    {
        var text = DiscordActivity.Text(new string('a', 200))!;

        Assert.Equal(DiscordActivity.MaxTextLength, text.Length);
        Assert.EndsWith("a…", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_cut_never_splits_a_character()
    {
        // 127 letters then emoji (two UTF-16 units each): the cut must not leave half an emoji behind.
        var text = DiscordActivity.Text(new string('a', 126) + string.Concat(Enumerable.Repeat("😀", 10)))!;

        Assert.True(text.Length <= DiscordActivity.MaxTextLength);
        Assert.False(char.IsHighSurrogate(text[^2]) && !char.IsLowSurrogate(text[^1]));
        Assert.Equal(new string('a', 126) + "…", text);
    }

    [Theory]
    [InlineData("https://i.ytimg.com/vi/x/hqdefault.jpg", true)]
    [InlineData("http://i.ytimg.com/vi/x/hqdefault.jpg", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void Only_https_images_are_sent(string url, bool kept)
    {
        Assert.Equal(kept ? url : null, DiscordActivity.ImageUrl(url));
    }

    [Fact]
    public void Image_links_over_256_characters_are_left_out()
    {
        Assert.Null(DiscordActivity.ImageUrl("https://example.com/" + new string('x', 240)));
    }

    [Fact]
    public void Other_hosts_keep_their_listed_cover()
    {
        var video = new Track
        {
            Title = "Video",
            VideoId = "v",
            Thumbnails = [new Thumbnail("https://i.ytimg.com/vi/v/mqdefault.jpg", 320, 180), new Thumbnail("https://i.ytimg.com/vi/v/maxresdefault.jpg", 1280, 720)],
        };

        Assert.Equal("https://i.ytimg.com/vi/v/maxresdefault.jpg", DiscordActivity.CoverUrl(video));
    }

    [Theory]
    [InlineData("123456789012345678", true)]
    [InlineData("12345678901234567890", true)]
    [InlineData("1234567890123456", false)]
    [InlineData(DiscordPresence.ApplicationId, true)]
    [InlineData("DISCORD_APPLICATION_ID", false)]
    [InlineData("", false)]
    public void Only_a_snowflake_counts_as_an_application_id(string id, bool valid)
    {
        Assert.Equal(valid, DiscordPresence.IsValidApplicationId(id));
    }
}

public sealed class PresenceCoalescerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 10, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_first_update_goes_out_at_once()
    {
        var coalescer = new PresenceCoalescer();
        coalescer.Set("A");

        Assert.True(coalescer.TryTake(T0, out var sent));
        Assert.Equal("A", sent);
        Assert.False(coalescer.HasPending);
    }

    [Fact]
    public void Quick_changes_collapse_into_the_latest_after_the_minimum_spacing()
    {
        var coalescer = new PresenceCoalescer();
        coalescer.Set("A");
        coalescer.TryTake(T0, out _);

        coalescer.Set("B");
        coalescer.Set("C");
        coalescer.Set("D");

        Assert.False(coalescer.TryTake(T0.AddSeconds(1), out _));
        Assert.Equal(T0 + PresenceCoalescer.MinSpacing, coalescer.DueAt(T0.AddSeconds(1)));
        Assert.True(coalescer.TryTake(T0 + PresenceCoalescer.MinSpacing, out var sent));
        Assert.Equal("D", sent);
        Assert.False(coalescer.HasPending);
    }

    [Fact]
    public void No_more_than_five_updates_in_twenty_seconds()
    {
        var coalescer = new PresenceCoalescer();
        var now = T0;
        var sends = new List<DateTimeOffset>();
        for (var i = 0; i < 40; i++)
        {
            coalescer.Set("update " + i);
            var due = coalescer.DueAt(now)!.Value;
            Assert.True(coalescer.TryTake(due, out _));
            sends.Add(due);
            now = due + TimeSpan.FromMilliseconds(100);
        }

        for (var i = PresenceCoalescer.MaxPerWindow; i < sends.Count; i++)
        {
            Assert.True(sends[i] - sends[i - PresenceCoalescer.MaxPerWindow] >= PresenceCoalescer.Window);
            Assert.True(sends[i] - sends[i - 1] >= PresenceCoalescer.MinSpacing);
        }
    }

    [Fact]
    public void An_update_equal_to_what_shows_is_not_sent_again()
    {
        var coalescer = new PresenceCoalescer();
        coalescer.Set("A");
        coalescer.TryTake(T0, out _);

        coalescer.Set("B");
        coalescer.Set("A"); // back to what Discord already shows

        Assert.False(coalescer.HasPending);
        Assert.Null(coalescer.DueAt(T0.AddMinutes(1)));
    }

    [Fact]
    public void Clearing_on_a_fresh_connection_sends_nothing()
    {
        var coalescer = new PresenceCoalescer();
        coalescer.Set(null);

        Assert.False(coalescer.HasPending);
    }

    [Fact]
    public void A_clear_is_an_update_once_something_showed()
    {
        var coalescer = new PresenceCoalescer();
        coalescer.Set("A");
        coalescer.TryTake(T0, out _);
        coalescer.Set(null);

        Assert.True(coalescer.PendingIsClear);
        Assert.True(coalescer.TryTake(T0.AddSeconds(5), out var sent));
        Assert.Null(sent);
    }

    [Fact]
    public void A_reconnect_sends_the_current_status_again_within_the_limit()
    {
        var coalescer = new PresenceCoalescer();
        coalescer.Set("A");
        coalescer.TryTake(T0, out _);

        coalescer.Reconnected("A");

        Assert.True(coalescer.HasPending);
        Assert.False(coalescer.TryTake(T0.AddSeconds(1), out _));
        Assert.True(coalescer.TryTake(T0 + PresenceCoalescer.MinSpacing, out var sent));
        Assert.Equal("A", sent);
    }
}
