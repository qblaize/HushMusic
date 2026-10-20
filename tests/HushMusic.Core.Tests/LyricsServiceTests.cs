using HushMusic.Core.Lyrics;
using HushMusic.Core.Models;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>Source order, fallback, caching and failures of <see cref="LyricsService"/>, on fakes (placeholder lyrics only).</summary>
public sealed class LyricsServiceTests : IDisposable
{
    private const string LrcLibSynced = """
        {"id":42,"trackName":"Test Song","artistName":"Test Artist","albumName":"Test Album","duration":200.0,"instrumental":false,"plainLyrics":"First test line\nSecond test line","syncedLyrics":"[00:10.00]First test line\n[00:20.00]Second test line"}
        """;

    private readonly LyricsTestHost _host = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Track Song(string videoId = "song0000001") => new()
    {
        Title = "Test Song (Official Audio)",
        VideoId = videoId,
        Artists = [new ArtistRef("Test Artist", "UC1")],
        Album = new AlbumRef("Test Album", "MPREb_1"),
        Duration = TimeSpan.FromSeconds(200),
    };

    private static Models.Lyrics TimedYouTube() => new()
    {
        Text = "First test line\nSecond test line",
        Source = "Source: Test Provider",
        TimedLines = [new(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), "First test line"), new(TimeSpan.FromSeconds(9), TimeSpan.FromSeconds(14), "Second test line")],
    };

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task The_first_enabled_source_with_lyrics_answers()
    {
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        _host.LrcLib.Answer(LrcLibSynced);
        var service = _host.CreateService();

        var lyrics = await service.GetLyricsAsync(Song(), Ct);

        Assert.NotNull(lyrics);
        Assert.Equal(LyricsSources.YouTubeSynced, lyrics.SourceKey);
        Assert.Equal("YouTube", lyrics.SourceName);
        Assert.Equal(LyricsSyncLevel.Line, lyrics.Sync);
        Assert.Equal("Source: Test Provider", lyrics.Attribution);
        Assert.Equal(TimeSpan.FromSeconds(9), lyrics.Lines[0].End);
        Assert.Equal(0, _host.Captions.Calls);
        Assert.Empty(_host.LrcLib.Requests);
    }

    [Fact]
    public async Task Sources_without_lyrics_fall_through_in_order()
    {
        _host.LrcLib.Answer(LrcLibSynced);
        var service = _host.CreateService();

        var lyrics = await service.GetLyricsAsync(Song(), Ct);

        Assert.Equal(LyricsSources.LrcLibSynced, lyrics?.SourceKey);
        Assert.Equal(1, _host.Watch.LyricsCalls);
        Assert.Equal(1, _host.Captions.Calls);
        Assert.Single(_host.LrcLib.Requests);

        // The title went to LRCLIB without its "(Official Audio)".
        Assert.Contains("track_name=Test%20Song&", _host.LrcLib.Requests[0].RequestUri!.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_users_order_and_switches_decide()
    {
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        _host.LrcLib.Answer(LrcLibSynced);
        var service = _host.CreateService();
        await service.SetSourceOrderAsync(
            [new(LyricsSources.YouTubeSynced, false), new(LyricsSources.LrcLibPlain, true), new(LyricsSources.LrcLibSynced, true)],
            Ct);

        var lyrics = await service.GetLyricsAsync(Song(), Ct);

        Assert.Equal(LyricsSources.LrcLibPlain, lyrics?.SourceKey);
        Assert.Equal(LyricsSyncLevel.Unsynced, lyrics?.Sync);
        Assert.Equal(0, _host.Watch.LyricsCalls);
        // The entries left out were merged in after their default predecessors.
        Assert.Equal(
            [LyricsSources.YouTubeSynced, LyricsSources.YouTubeCaptions, LyricsSources.LrcLibWord, LyricsSources.LrcLibPlain, LyricsSources.LrcLibSynced, LyricsSources.YouTubePlain],
            service.GetSourceOrder().Select(s => s.Key));
        Assert.Equal(6, _host.Settings.Current.LyricsSources.Count);
    }

    [Fact]
    public async Task One_request_serves_every_entry_of_a_source()
    {
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        var service = _host.CreateService();

        await service.GetLyricsAsync(Song(), Ct);
        var plain = await service.GetLyricsAsync(Song(), LyricsSources.YouTubePlain, Ct);
        var available = await service.GetAvailableSourcesAsync(Song(), Ct);

        Assert.Equal(LyricsSyncLevel.Unsynced, plain?.Sync);
        Assert.Equal(["First test line", "Second test line"], plain?.Lines.Select(l => l.Text));
        Assert.Equal(1, _host.Watch.LyricsCalls);
        Assert.Equal([LyricsSources.YouTubeSynced, LyricsSources.YouTubePlain], available.Select(s => s.Key));
    }

    [Fact]
    public async Task A_specific_source_is_fetched_on_demand_and_unknown_keys_have_nothing()
    {
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        _host.LrcLib.Answer(LrcLibSynced);
        var service = _host.CreateService();

        var lrclib = await service.GetLyricsAsync(Song(), LyricsSources.LrcLibSynced, Ct);

        Assert.Equal("LRCLIB", lrclib?.SourceName);
        Assert.Equal(0, _host.Watch.LyricsCalls);
        Assert.Null(await service.GetLyricsAsync(Song(), "musixmatch", Ct));
        Assert.Null(await service.GetLyricsAsync(Song(), LyricsSources.LrcLibWord, Ct));
        Assert.Single(_host.LrcLib.Requests);
    }

    [Fact]
    public async Task Answers_are_cached_in_memory_and_on_disk()
    {
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        var first = await _host.CreateService().GetLyricsAsync(Song(), Ct);

        // A new service (the next app start) reads the cache file instead of asking again.
        var restarted = _host.CreateService();
        var again = await restarted.GetLyricsAsync(Song(), Ct);
        var available = await restarted.GetAvailableSourcesAsync(Song(), Ct);

        Assert.Equal(1, _host.Watch.LyricsCalls);
        Assert.Equal(first!.Lines, again!.Lines);
        Assert.Equal(first.Attribution, again.Attribution);
        Assert.Equal(2, available.Count);
        Assert.True(File.Exists(Path.Combine(_host.Paths.LyricsFolder, "song0000001.json")));
    }

    [Fact]
    public async Task Missing_lyrics_are_remembered_for_an_hour_found_ones_for_a_week()
    {
        var service = _host.CreateService();
        Assert.Null(await service.GetLyricsAsync(Song(), Ct));
        Assert.Null(await service.GetLyricsAsync(Song(), Ct));
        Assert.Equal(1, _host.Watch.LyricsCalls);
        Assert.Equal(["/api/get", "/api/search"], _host.LrcLib.Requests.Select(r => r.RequestUri!.AbsolutePath));

        _host.Clock.Now += TimeSpan.FromMinutes(61);
        _host.LrcLib.Answer(LrcLibSynced);
        Assert.Equal(LyricsSources.LrcLibSynced, (await service.GetLyricsAsync(Song(), Ct))?.SourceKey);
        Assert.Equal(3, _host.LrcLib.Requests.Count);

        _host.Clock.Now += TimeSpan.FromDays(6);
        await service.GetLyricsAsync(Song(), LyricsSources.LrcLibSynced, Ct);
        Assert.Equal(3, _host.LrcLib.Requests.Count);

        _host.Clock.Now += TimeSpan.FromDays(2);
        await service.GetLyricsAsync(Song(), LyricsSources.LrcLibSynced, Ct);
        Assert.Equal(4, _host.LrcLib.Requests.Count);
    }

    [Fact]
    public async Task Live_radio_never_reaches_a_source()
    {
        var station = Song() with
        {
            VideoId = "radio:abc",
            Type = TrackType.Video,
            Station = new RadioStation { Id = "abc", Name = "Test FM", StreamUrl = "https://radio.example/stream" },
        };
        var service = _host.CreateService();

        Assert.Null(await service.GetLyricsAsync(station, Ct));
        Assert.Null(await service.GetLyricsAsync(station, LyricsSources.LrcLibSynced, Ct));
        Assert.Empty(await service.GetAvailableSourcesAsync(station, Ct));
        Assert.Equal(0, _host.Watch.LyricsCalls);
        Assert.Equal(0, _host.Captions.Calls);
        Assert.Empty(_host.LrcLib.Requests);
    }

    [Fact]
    public async Task Episodes_are_only_looked_up_on_youtube()
    {
        var service = _host.CreateService();

        Assert.Null(await service.GetLyricsAsync(Song() with { Type = TrackType.Episode }, Ct));
        Assert.Equal(1, _host.Watch.LyricsCalls);
        Assert.Equal(0, _host.Captions.Calls);
        Assert.Empty(_host.LrcLib.Requests);
    }

    [Fact]
    public async Task A_failing_source_is_skipped_and_retried_later()
    {
        _host.Watch.Failure = new HushException("offline");
        _host.LrcLib.Answer(LrcLibSynced);
        var service = _host.CreateService();

        Assert.Equal(LyricsSources.LrcLibSynced, (await service.GetLyricsAsync(Song(), Ct))?.SourceKey);

        _host.Watch.Failure = null;
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        Assert.Equal(LyricsSources.YouTubeSynced, (await service.GetLyricsAsync(Song(), Ct))?.SourceKey);
        Assert.Equal(2, _host.Watch.LyricsCalls);
    }

    [Fact]
    public async Task When_no_source_can_be_reached_the_error_is_reported()
    {
        _host.Watch.Failure = new HttpRequestException("offline");
        _host.Captions.Failure = new HttpRequestException("offline");
        _host.LrcLib.Respond = _ => throw new HttpRequestException("offline");
        var service = _host.CreateService();

        await Assert.ThrowsAsync<HushException>(() => service.GetLyricsAsync(Song(), Ct));
        await Assert.ThrowsAsync<HushException>(() => service.GetLyricsAsync(Song(), LyricsSources.YouTubeSynced, Ct));

        // One source answering "none" is an answer: no error then.
        _host.Captions.Failure = null;
        Assert.Null(await service.GetLyricsAsync(Song(), Ct));
    }

    [Fact]
    public async Task Callers_asking_at_the_same_time_share_one_request()
    {
        _host.Watch.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        var service = _host.CreateService();

        var first = service.GetLyricsAsync(Song(), Ct);
        var second = service.GetLyricsAsync(Song(), LyricsSources.YouTubePlain, Ct);
        await Eventually.TrueAsync(() => _host.Watch.LyricsCalls == 1);
        _host.Watch.Gate.SetResult();

        Assert.Equal(LyricsSources.YouTubeSynced, (await first)?.SourceKey);
        Assert.Equal(LyricsSources.YouTubePlain, (await second)?.SourceKey);
        Assert.Equal(1, _host.Watch.LyricsCalls);
    }

    [Fact]
    public async Task A_cancelled_caller_does_not_cancel_the_shared_request()
    {
        _host.Watch.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _host.Watch.Lyrics["MPLYsong0000001"] = TimedYouTube();
        var service = _host.CreateService();
        using var cts = new CancellationTokenSource();

        var cancelled = service.GetLyricsAsync(Song(), cts.Token);
        await Eventually.TrueAsync(() => _host.Watch.LyricsCalls == 1);
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        _host.Watch.Gate.SetResult();

        await Eventually.TrueAsync(() => File.Exists(Path.Combine(_host.Paths.LyricsFolder, "song0000001.json")));
        Assert.Equal(LyricsSources.YouTubeSynced, (await service.GetLyricsAsync(Song(), Ct))?.SourceKey);
        Assert.Equal(1, _host.Watch.LyricsCalls);
    }

    [Fact]
    public async Task Captions_in_a_confirmed_language_are_used_and_likely_translations_are_not()
    {
        var lines = new LyricLine[]
        {
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), "♪ First test line ♪", []),
            new(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), "♪ Second test line ♪", []),
        };
        _host.Captions.Captions["video000001"] = new CaptionLyrics("en", lines) { LanguageConfirmed = false, TrackName = "English" };
        _host.Captions.Captions["video000002"] = new CaptionLyrics("en", lines) { LanguageConfirmed = false };
        _host.Captions.Captions["video000003"] = new CaptionLyrics("en", lines) { LanguageConfirmed = true };
        var service = _host.CreateService();

        var latinTitle = await service.GetLyricsAsync(Song("video000001") with { Type = TrackType.Video }, LyricsSources.YouTubeCaptions, Ct);
        var translated = await service.GetLyricsAsync(Song("video000002") with { Title = "テストの歌", Type = TrackType.Video }, LyricsSources.YouTubeCaptions, Ct);
        var confirmed = await service.GetLyricsAsync(Song("video000003") with { Title = "テストの歌", Type = TrackType.Video }, LyricsSources.YouTubeCaptions, Ct);

        Assert.Equal(["First test line", "Second test line"], latinTitle?.Lines.Select(l => l.Text));
        Assert.Equal("YouTube captions", latinTitle?.SourceName);
        Assert.Equal("https://www.youtube.com/watch?v=video000001", latinTitle?.SourceUrl);
        Assert.Null(translated);
        Assert.NotNull(confirmed);
    }

    [Fact]
    public async Task Order_changes_are_announced_whoever_saves_them()
    {
        var service = _host.CreateService();
        var raised = 0;
        service.SourceOrderChanged += (_, _) => raised++;

        var reversed = LyricsSources.All.Reverse().Select(s => new LyricsSourceSetting(s.Key, true)).ToList();
        await service.SetSourceOrderAsync(reversed, Ct);
        Assert.Equal(1, raised);
        Assert.Equal(LyricsSources.LrcLibPlain, service.GetSourceOrder()[0].Key);

        // Saving the same order again, or a partial one that merges back to it, changes nothing.
        await service.SetSourceOrderAsync(service.GetSourceOrder(), Ct);
        Assert.Equal(1, raised);

        await _host.Settings.UpdateAsync(s => s.LyricsSources = [reversed[0] with { Enabled = false }, .. reversed.Skip(1)], Ct);
        Assert.Equal(2, raised);
        Assert.False(service.GetSourceOrder()[0].Enabled);

        await _host.Settings.UpdateAsync(s => s.Volume = 0.3, Ct);
        Assert.Equal(2, raised);
    }
}
