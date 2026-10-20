using System.Net;
using System.Text.Json.Nodes;
using HushMusic.Core.Lyrics;
using Xunit;

namespace HushMusic.Core.Tests;

/// <summary>
/// LRCLIB records, matching and the client. The JSON keeps the shape of real LRCLIB answers (2026-10-20); the lyrics in
/// it are placeholder lines.
/// </summary>
public sealed class LrcLibTests
{
    // /api/get answered this placeholder entry (one line, "probe") for a well-known song.
    private const string GetPlaceholder = """
        {"id":1009665,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Test Album","duration":214.0,"instrumental":false,"hasWordSync":false,"plainLyrics":"probe","syncedLyrics":"[00:00.00]probe","lyricsfile":"version: '1.0'\nmetadata:\n  title: Test Song\n  artist: Test Artist\n  instrumental: false\nlines:\n- text: probe\n  start_ms: 0\nplain: probe\n"}
        """;

    private const string GetSynced = """
        {"id":42,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Test Album","duration":202.0,"instrumental":false,"hasWordSync":false,"plainLyrics":"First test line\n\nSecond test line\nThird test line","syncedLyrics":"[00:13.42] First test line\n[00:14.81] \n[00:26.95] Second test line\n[00:29.83] Third test line"}
        """;

    private const string GetPlainOnly = """
        {"id":43,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Test Album","duration":202.0,"instrumental":false,"plainLyrics":"First test line\nSecond test line","syncedLyrics":null}
        """;

    private const string GetInstrumental = """
        {"id":44,"name":"Test Tune","trackName":"Test Tune","artistName":"Test Artist","albumName":null,"duration":180.0,"instrumental":true,"plainLyrics":null,"syncedLyrics":null}
        """;

    private const string Search = """
        [
          {"id":37015057,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Compilation","duration":202.0,"instrumental":false,"hasWordSync":false,"plainLyrics":"First test line\nSecond test line","syncedLyrics":"[00:13.42] First test line\n[00:26.95] Second test line"},
          {"id":36936735,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Extended Mix","duration":248.0,"instrumental":false,"hasWordSync":false,"plainLyrics":"First test line\nSecond test line","syncedLyrics":"[00:13.42] First test line\n[00:26.95] Second test line"},
          {"id":37496087,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Party Mix","duration":200.0,"instrumental":false,"plainLyrics":"First test line\nSecond test line","syncedLyrics":"[00:13.42]First test line\n[00:26.95]Second test line"},
          {"id":5,"name":"Test Song (Remix)","trackName":"Test Song (Remix)","artistName":"Someone Else","albumName":"Remixes","duration":201.0,"instrumental":false,"plainLyrics":"Other line\nAnother line","syncedLyrics":"[00:01.00]Other line\n[00:02.00]Another line"},
          {"id":6,"name":"Test Song","trackName":"Test Song","artistName":"Test Artist","albumName":"Live","duration":201.0,"instrumental":false,"plainLyrics":"probe","syncedLyrics":"[00:00.00]probe"}
        ]
        """;

    private static readonly LyricsQuery Query = new("Test Song", "Test Artist", "Test Album", TimeSpan.FromSeconds(201));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void A_record_is_read()
    {
        var record = LrcLib.ParseRecord(JsonNode.Parse(GetSynced));

        Assert.NotNull(record);
        Assert.Equal(42, record.Id);
        Assert.Equal("Test Song", record.TrackName);
        Assert.Equal("Test Artist", record.ArtistName);
        Assert.Equal(TimeSpan.FromSeconds(202), record.Duration);
        Assert.False(record.Instrumental);
        Assert.True(record.HasSynced);
        Assert.Equal(5, LrcLib.ParseSearch(Search).Count);
        Assert.Empty(LrcLib.ParseSearch("not json"));
        Assert.Null(LrcLib.ParseRecord(JsonNode.Parse("""{"code":404,"name":"TrackNotFound"}""")));
    }

    [Fact]
    public void Placeholders_rank_below_real_lyrics()
    {
        Assert.Equal(-1, LrcLib.LyricsRank(LrcLib.ParseRecord(JsonNode.Parse(GetPlaceholder))!));
        Assert.Equal(2, LrcLib.LyricsRank(LrcLib.ParseRecord(JsonNode.Parse(GetSynced))!));
        Assert.Equal(1, LrcLib.LyricsRank(LrcLib.ParseRecord(JsonNode.Parse(GetPlainOnly))!));
        Assert.Equal(0, LrcLib.LyricsRank(LrcLib.ParseRecord(JsonNode.Parse(GetInstrumental))!));
    }

    [Fact]
    public void The_best_search_result_has_the_same_title_artist_and_length()
    {
        var results = LrcLib.ParseSearch(Search);

        Assert.Equal(37015057, LrcLib.PickBest(results, Query)?.Id);
        Assert.Equal(36936735, LrcLib.PickBest(results, Query with { Duration = TimeSpan.FromSeconds(250) })?.Id);
        Assert.Null(LrcLib.PickBest(results, Query with { Duration = TimeSpan.FromSeconds(230) }));
        Assert.Null(LrcLib.PickBest(results, Query with { Artist = "Nobody Here" }));

        // Without the track's length only an exact title is trusted.
        Assert.NotNull(LrcLib.PickBest(results, Query with { Duration = null }));
        Assert.Null(LrcLib.PickBest(results, Query with { Title = "Test Songs Remixed", Duration = null }));
    }

    [Fact]
    public void A_record_fills_the_synced_and_plain_entries()
    {
        var documents = LrcLib.ToDocuments(LrcLib.ParseRecord(JsonNode.Parse(GetSynced))!, TimeSpan.FromSeconds(202));

        Assert.Equal([LyricsSources.LrcLibSynced, LyricsSources.LrcLibPlain], documents.Select(d => d.SourceKey));
        var synced = documents[0];
        Assert.Equal(LyricsSyncLevel.Line, synced.Sync);
        Assert.Equal("LRCLIB", synced.SourceName);
        Assert.Equal(["First test line", "Second test line", "Third test line"], synced.Lines.Select(l => l.Text));
        Assert.Equal(TimeSpan.FromSeconds(14.81), synced.Lines[0].End);
        Assert.Equal(TimeSpan.FromSeconds(202), synced.Lines[^1].End);
        Assert.Equal(["First test line", string.Empty, "Second test line", "Third test line"], documents[1].Lines.Select(l => l.Text));
    }

    [Fact]
    public void Word_tags_fill_the_word_entry_too()
    {
        var record = new LrcLibRecord(1, "Test Song", "Test Artist", null, TimeSpan.FromSeconds(30))
        {
            SyncedLyrics = "[00:01.00]<00:01.00>One <00:01.50>test <00:02.00>line<00:03.00>\n[00:04.00]<00:04.00>Two <00:04.40>more<00:05.00>",
        };

        var documents = LrcLib.ToDocuments(record, null);

        Assert.Equal([LyricsSources.LrcLibWord, LyricsSources.LrcLibSynced, LyricsSources.LrcLibPlain], documents.Select(d => d.SourceKey));
        Assert.Equal(LyricsSyncLevel.Word, documents[0].Sync);
        Assert.Equal(3, documents[0].Lines[0].Words.Count);
        Assert.All(documents[1].Lines, l => Assert.Empty(l.Words));
        Assert.Equal(["One test line", "Two more"], documents[2].Lines.Select(l => l.Text));
    }

    [Fact]
    public void An_instrumental_record_is_an_empty_instrumental_document()
    {
        var documents = LrcLib.ToDocuments(LrcLib.ParseRecord(JsonNode.Parse(GetInstrumental))!, null);

        Assert.Equal([LyricsSources.LrcLibSynced, LyricsSources.LrcLibPlain], documents.Select(d => d.SourceKey));
        Assert.All(documents, d => Assert.True(d.IsInstrumental));
        Assert.All(documents, d => Assert.Empty(d.Lines));
    }

    [Fact]
    public async Task An_exact_synced_match_needs_one_request()
    {
        var lrclib = new LyricsFakeLrcLib();
        lrclib.Answer(GetSynced);

        var record = await lrclib.CreateClient().FindAsync(Query, Ct);

        Assert.Equal(42, record?.Id);
        var request = Assert.Single(lrclib.Requests);
        Assert.Equal("https://lrclib.net/api/get?track_name=Test%20Song&artist_name=Test%20Artist&album_name=Test%20Album&duration=201", request.RequestUri!.AbsoluteUri);
        var agent = request.Headers.UserAgent.ToString();
        Assert.StartsWith("Hush/", agent, StringComparison.Ordinal);
        Assert.EndsWith("(https://github.com/qblaize/HushMusic)", agent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_exact_match_falls_back_to_search()
    {
        var lrclib = new LyricsFakeLrcLib();
        lrclib.Answer(get: null, Search);

        var record = await lrclib.CreateClient().FindAsync(Query with { Title = "Test Song (Remastered 2020)" }, Ct);

        Assert.Equal(37015057, record?.Id);
        Assert.Equal(2, lrclib.Requests.Count);
        Assert.Equal("https://lrclib.net/api/search?track_name=Test%20Song&artist_name=Test%20Artist", lrclib.Requests[1].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task A_placeholder_or_plain_only_match_is_replaced_by_a_synced_search_result()
    {
        var lrclib = new LyricsFakeLrcLib();
        lrclib.Answer(GetPlaceholder, Search);
        Assert.Equal(37015057, (await lrclib.CreateClient().FindAsync(Query, Ct))?.Id);

        lrclib.Answer(GetPlainOnly, Search);
        Assert.Equal(37015057, (await lrclib.CreateClient().FindAsync(Query, Ct))?.Id);

        lrclib.Answer(GetPlainOnly, "[]");
        Assert.Equal(43, (await lrclib.CreateClient().FindAsync(Query, Ct))?.Id);

        lrclib.Answer(GetPlaceholder, "[]");
        Assert.Null(await lrclib.CreateClient().FindAsync(Query, Ct));
    }

    [Fact]
    public async Task Server_errors_are_reported()
    {
        var lrclib = new LyricsFakeLrcLib { Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) };

        var error = await Assert.ThrowsAsync<HushException>(() => lrclib.CreateClient().FindAsync(Query, Ct));
        Assert.Contains("503", error.Message, StringComparison.Ordinal);
        Assert.Equal(2, lrclib.Requests.Count); // a busy server is asked once more

        var calls = 0;
        lrclib.Respond = _ => ++calls == 1 ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) : LyricsFakeLrcLib.Json(GetSynced);
        Assert.Equal(42, (await lrclib.CreateClient().FindAsync(Query, Ct))?.Id);

        lrclib.Respond = _ => throw new HttpRequestException("offline");
        await Assert.ThrowsAsync<HushException>(() => lrclib.CreateClient().FindAsync(Query, Ct));
    }
}
