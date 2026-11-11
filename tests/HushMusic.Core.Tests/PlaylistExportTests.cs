using System.Text;
using HushMusic.Core.Models;
using HushMusic.Core.Services;
using Xunit;

namespace HushMusic.Core.Tests;

public sealed class PlaylistExportTests
{
    private const string Header = "Title,Artist,Album,Duration,Video ID,URL\r\n";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Csv_has_a_header_and_one_plain_row_per_song()
    {
        var csv = Csv(Song("abc123", "Midnight City", ["M83"], "Hurry Up, We're Dreaming", TimeSpan.FromSeconds(243)));

        Assert.Equal(
            Header + "Midnight City,M83,\"Hurry Up, We're Dreaming\",4:03,abc123,https://music.youtube.com/watch?v=abc123\r\n",
            csv);
    }

    [Fact]
    public void Csv_quotes_commas_quotes_and_line_breaks()
    {
        var csv = Csv(Song("id1", "Say \"Hello\", World", ["A"], null, TimeSpan.FromSeconds(60)), Song("id2", "Two\nlines", ["B"], null, null), Song("id3", "Back\r\nslash", ["C"], null, null));

        Assert.Equal(
            Header
            + "\"Say \"\"Hello\"\", World\",A,,1:00,id1,https://music.youtube.com/watch?v=id1\r\n"
            + "\"Two\nlines\",B,,,id2,https://music.youtube.com/watch?v=id2\r\n"
            + "\"Back\r\nslash\",C,,,id3,https://music.youtube.com/watch?v=id3\r\n",
            csv);
    }

    [Fact]
    public void Csv_joins_several_artists_in_one_quoted_field()
    {
        var csv = Csv(Song("id", "Duet", ["Alpha", "Beta", "Gamma"], "Album", TimeSpan.FromSeconds(185)));

        Assert.Equal(Header + "Duet,\"Alpha, Beta, Gamma\",Album,3:05,id,https://music.youtube.com/watch?v=id\r\n", csv);
    }

    [Fact]
    public void Csv_leaves_missing_artist_album_and_duration_empty()
    {
        var csv = Csv(Song("id", "Unknown", [], null, null));

        Assert.Equal(Header + "Unknown,,,,id,https://music.youtube.com/watch?v=id\r\n", csv);
    }

    [Fact]
    public void Csv_with_no_songs_is_just_the_header() => Assert.Equal(Header, Csv());

    [Fact]
    public void M3u_lists_seconds_artist_title_and_link()
    {
        var m3u = M3u(
            Song("abc", "Midnight City", ["M83"], "Album", TimeSpan.FromSeconds(243.9)),
            Song("def", "Duet", ["Alpha", "Beta"], null, TimeSpan.FromSeconds(185)));

        Assert.Equal(
            "#EXTM3U\r\n"
            + "#EXTINF:243,M83 - Midnight City\r\nhttps://music.youtube.com/watch?v=abc\r\n"
            + "#EXTINF:185,Alpha, Beta - Duet\r\nhttps://music.youtube.com/watch?v=def\r\n",
            m3u);
    }

    [Fact]
    public void M3u_marks_an_unknown_length_and_keeps_each_entry_on_one_line()
    {
        var m3u = M3u(Song("id", "Two\r\nlines", [], null, null), Song("id2", "Live", ["DJ\nX"], null, TimeSpan.Zero));

        Assert.Equal(
            "#EXTM3U\r\n"
            + "#EXTINF:-1,Two lines\r\nhttps://music.youtube.com/watch?v=id\r\n"
            + "#EXTINF:-1,DJ X - Live\r\nhttps://music.youtube.com/watch?v=id2\r\n",
            m3u);
    }

    [Fact]
    public async Task Csv_files_start_with_a_byte_order_mark_and_m3u8_files_do_not()
    {
        var tracks = new[] { Song("id", "Café", ["Zoë"], null, TimeSpan.FromSeconds(61)) };

        using var csv = new MemoryStream();
        await PlaylistExport.WriteAsync(csv, PlaylistExportFormat.Csv, tracks, Ct);
        using var m3u = new MemoryStream();
        await PlaylistExport.WriteAsync(m3u, PlaylistExportFormat.M3u, tracks, Ct);

        Assert.Equal([0xEF, 0xBB, 0xBF], csv.ToArray()[..3]);
        Assert.Equal(Header + "Café,Zoë,,1:01,id,https://music.youtube.com/watch?v=id\r\n", Encoding.UTF8.GetString(csv.ToArray()[3..]));
        Assert.Equal((byte)'#', m3u.ToArray()[0]);
        Assert.Contains("#EXTINF:61,Zoë - Café", Encoding.UTF8.GetString(m3u.ToArray()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(59, "0:59")]
    [InlineData(60, "1:00")]
    [InlineData(4265, "71:05")]
    public void Duration_is_minutes_and_seconds(int seconds, string expected) =>
        Assert.Equal(expected, PlaylistExport.DurationText(seconds == 0 ? null : TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData("My mix", "My mix")]
    [InlineData("AC/DC: Best of?", "AC DC Best of")]
    [InlineData("  ...  ", "Playlist")]
    [InlineData("Ends with dots...", "Ends with dots")]
    [InlineData("CON", "CON_")]
    [InlineData(null, "Playlist")]
    public void File_names_drop_characters_windows_does_not_allow(string? title, string expected) =>
        Assert.Equal(expected, PlaylistExport.FileNameFor(title));

    [Theory]
    [InlineData("a.csv", PlaylistExportFormat.Csv)]
    [InlineData(@"C:\x\b.M3U8", PlaylistExportFormat.M3u)]
    [InlineData("c.m3u", PlaylistExportFormat.M3u)]
    [InlineData("d.txt", null)]
    public void The_format_follows_the_extension(string path, PlaylistExportFormat? expected) =>
        Assert.Equal(expected, PlaylistExport.FormatOf(path));

    [Fact]
    public async Task Loading_follows_continuations_to_the_end()
    {
        var pages = new Dictionary<string, Paged<int>>
        {
            ["p2"] = new([3, 4], "p3"),
            ["p3"] = new([5], null),
        };
        var reported = new List<int>();

        var all = await PlaylistExport.LoadAllAsync(new Paged<int>([1, 2], "p2"), (c, _) => Task.FromResult(pages[c]), new SyncProgress(reported), Ct);

        Assert.Equal([1, 2, 3, 4, 5], all);
        Assert.Equal([2, 4, 5], reported);
    }

    [Fact]
    public async Task Loading_stops_when_a_continuation_repeats_or_pages_stay_empty()
    {
        var calls = 0;
        var looping = await PlaylistExport.LoadAllAsync(new Paged<int>([1], "same"), (_, _) =>
        {
            calls++;
            return Task.FromResult(new Paged<int>([2], "same"));
        }, cancellationToken: Ct);

        Assert.Equal([1, 2], looping);
        Assert.Equal(1, calls);

        var n = 0;
        var empty = await PlaylistExport.LoadAllAsync(new Paged<int>([1], "c0"), (_, _) => Task.FromResult(new Paged<int>([], "c" + ++n)), cancellationToken: Ct);

        Assert.Equal([1], empty);
        Assert.Equal(3, n);
    }

    private static string Csv(params Track[] tracks)
    {
        var writer = new StringWriter();
        PlaylistExport.WriteCsv(writer, tracks);
        return writer.ToString();
    }

    private static string M3u(params Track[] tracks)
    {
        var writer = new StringWriter();
        PlaylistExport.WriteM3u(writer, tracks);
        return writer.ToString();
    }

    private static Track Song(string id, string title, string[] artists, string? album, TimeSpan? duration) => new()
    {
        VideoId = id,
        Title = title,
        Artists = [.. artists.Select(a => new ArtistRef(a, null))],
        Album = album is null ? null : new AlbumRef(album, null),
        Duration = duration,
    };

    private sealed class SyncProgress(List<int> values) : IProgress<int>
    {
        public void Report(int value) => values.Add(value);
    }
}
