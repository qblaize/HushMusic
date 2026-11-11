using System.Globalization;
using System.Text;
using HushMusic.Core.Models;

namespace HushMusic.Core.Services;

/// <summary>The file types a song list can be exported as.</summary>
public enum PlaylistExportFormat
{
    /// <summary>Comma-separated values (RFC 4180) in UTF-8 with a byte order mark, so Excel reads accents right.</summary>
    Csv,

    /// <summary>An extended M3U playlist in UTF-8 (.m3u8), one YouTube Music link per song.</summary>
    M3u,
}

/// <summary>
/// Writes a song list as CSV (Title, Artist, Album, Duration, Video ID, URL) or as an extended M3U playlist, and loads
/// every page of a long list first (<see cref="LoadAllAsync{T}"/>).
/// </summary>
public static class PlaylistExport
{
    public const string WatchUrlPrefix = "https://music.youtube.com/watch?v=";

    /// <summary>Stops a list whose pages never end (a continuation that keeps coming back) at 100,000 songs or so.</summary>
    public const int MaxPages = 1000;

    private const string LineEnd = "\r\n";

    private static readonly string[] CsvHeader = ["Title", "Artist", "Album", "Duration", "Video ID", "URL"];

    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>The format for a file name's extension (.csv, .m3u8 or .m3u), or null for anything else.</summary>
    public static PlaylistExportFormat? FormatOf(string? path) => Path.GetExtension(path ?? string.Empty).ToUpperInvariant() switch
    {
        ".CSV" => PlaylistExportFormat.Csv,
        ".M3U8" or ".M3U" => PlaylistExportFormat.M3u,
        _ => null,
    };

    /// <summary>".csv" or ".m3u8".</summary>
    public static string ExtensionOf(PlaylistExportFormat format) => format == PlaylistExportFormat.Csv ? ".csv" : ".m3u8";

    /// <summary>UTF-8; with a byte order mark for CSV (Excel needs it), without for M3U.</summary>
    public static Encoding EncodingOf(PlaylistExportFormat format) => new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == PlaylistExportFormat.Csv);

    public static string WatchUrl(string videoId) => WatchUrlPrefix + Uri.EscapeDataString(videoId);

    /// <summary>"3:07", "71:05" for long mixes; empty when the length isn't known.</summary>
    public static string DurationText(TimeSpan? duration)
    {
        if (duration is not { } value || value <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        var seconds = (long)value.TotalSeconds;
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>A file name (without extension) for a list title: characters Windows doesn't allow are dropped.</summary>
    public static string FileNameFor(string? title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['<', '>', ':', '"', '/', '\\', '|', '?', '*']).ToHashSet();
        var builder = new StringBuilder();
        foreach (var c in title ?? string.Empty)
        {
            builder.Append(invalid.Contains(c) || char.IsControl(c) ? ' ' : c);
        }

        var name = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim().TrimEnd('.', ' ');
        if (name.Length > 120)
        {
            name = name[..120].TrimEnd('.', ' ');
        }

        if (name.Length == 0)
        {
            return "Playlist";
        }

        return ReservedFileNames.Contains(name) ? name + "_" : name;
    }

    public static void Write(TextWriter writer, PlaylistExportFormat format, IEnumerable<Track> tracks)
    {
        if (format == PlaylistExportFormat.Csv)
        {
            WriteCsv(writer, tracks);
        }
        else
        {
            WriteM3u(writer, tracks);
        }
    }

    /// <summary>Header row, then one row per song. Fields with commas, quotes or line breaks are quoted (RFC 4180).</summary>
    public static void WriteCsv(TextWriter writer, IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tracks);
        WriteCsvRow(writer, CsvHeader);
        foreach (var track in tracks)
        {
            WriteCsvRow(writer, [track.Title, track.ArtistsText, track.Album?.Name, DurationText(track.Duration), track.VideoId, WatchUrl(track.VideoId)]);
        }
    }

    /// <summary><c>#EXTM3U</c>, then <c>#EXTINF:&lt;seconds&gt;,&lt;Artist&gt; - &lt;Title&gt;</c> and the link for each song (-1 when the length isn't known).</summary>
    public static void WriteM3u(TextWriter writer, IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(tracks);
        writer.Write("#EXTM3U" + LineEnd);
        foreach (var track in tracks)
        {
            var seconds = track.Duration is { } duration && duration > TimeSpan.Zero ? (long)duration.TotalSeconds : -1;
            var title = OneLine(track.Title);
            var artists = OneLine(track.ArtistsText);
            var name = artists.Length == 0 ? title : $"{artists} - {title}";
            writer.Write(string.Create(CultureInfo.InvariantCulture, $"#EXTINF:{seconds},{name}{LineEnd}"));
            writer.Write(WatchUrl(track.VideoId) + LineEnd);
        }
    }

    /// <summary>Writes the file in its format's encoding. The stream is left open.</summary>
    public static async Task WriteAsync(Stream stream, PlaylistExportFormat format, IReadOnlyList<Track> tracks, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        await using var writer = new StreamWriter(stream, EncodingOf(format), bufferSize: 64 * 1024, leaveOpen: true);
        var text = new StringWriter(CultureInfo.InvariantCulture);
        Write(text, format, tracks);
        await writer.WriteAsync(text.GetStringBuilder(), cancellationToken).ConfigureAwait(false);
        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The first page and every page after it, in order. Stops at the end of the list, when a continuation comes back
    /// again, after three empty pages in a row, or after <see cref="MaxPages"/> pages.
    /// </summary>
    /// <param name="progress">Told how many items have loaded after each page.</param>
    public static async Task<IReadOnlyList<T>> LoadAllAsync<T>(
        Paged<T> first,
        Func<string, CancellationToken, Task<Paged<T>>> next,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(next);
        List<T> items = [.. first.Items];
        progress?.Report(items.Count);
        HashSet<string> seen = new(StringComparer.Ordinal);
        var continuation = first.Continuation;
        var emptyPages = 0;
        for (var page = 1; continuation is not null && page < MaxPages && emptyPages < 3 && seen.Add(continuation); page++)
        {
            var result = await next(continuation, cancellationToken).ConfigureAwait(false);
            items.AddRange(result.Items);
            emptyPages = result.Items.Count == 0 ? emptyPages + 1 : 0;
            continuation = result.Continuation;
            progress?.Report(items.Count);
        }

        return items;
    }

    private static void WriteCsvRow(TextWriter writer, IReadOnlyList<string?> fields)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(CsvField(fields[i]));
        }

        writer.Write(LineEnd);
    }

    private static string CsvField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.AsSpan().IndexOfAny(",\"\r\n") < 0 ? value : "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    // An M3U entry is one line: line breaks in a title become spaces.
    private static string OneLine(string? value) =>
        string.Join(' ', (value ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
