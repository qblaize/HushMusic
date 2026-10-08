using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features.Stats;

/// <summary>The local, append-only log of plays behind "Your stats". Thread-safe.</summary>
public interface IPlayLog
{
    /// <summary>Raised after a line was added or the log was cleared (on the writing thread).</summary>
    event EventHandler? Changed;

    Task AppendAsync(PlayRecord record, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every play that started at or after <paramref name="since"/> (all when null), one record per play (the line with the
    /// most listening time), oldest first. Unreadable lines are skipped.
    /// </summary>
    Task<IReadOnlyList<PlayRecord>> ReadAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes the whole log.</summary>
    Task ClearAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// JSON Lines files, one per year (<c>history\plays-2026.jsonl</c>), only ever appended to. A crash can at worst leave one
/// partial last line, which readers skip and the next write starts after. Writers take turns on a file, so another copy of
/// the app writing at the same time can't interleave or overwrite lines.
/// </summary>
internal sealed partial class JsonLinesPlayLog : IPlayLog
{
    internal const string FolderName = "history";
    private const string FilePrefix = "plays-";
    private const string FileExtension = ".jsonl";
    private const int WriteAttempts = 40;

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Accented titles stay readable in the file instead of becoming \u escapes.
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<JsonLinesPlayLog> _logger;

    public JsonLinesPlayLog(IAppPaths paths, ILogger<JsonLinesPlayLog> logger)
        : this(Path.Combine(paths.Root, FolderName), logger)
    {
    }

    internal JsonLinesPlayLog(string folder, ILogger<JsonLinesPlayLog> logger)
    {
        Folder = folder;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public string Folder { get; }

    public async Task AppendAsync(PlayRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, JsonOptions) + "\n");
        var path = FileFor(record.Start.Year);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Folder);
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    WriteLine(path, line);
                    break;
                }
                catch (IOException ex) when (attempt < WriteAttempts && ex is not DirectoryNotFoundException)
                {
                    // A sharing violation: another copy of the app (or a scanner) has the file for a moment.
                    await Task.Delay(5 + Random.Shared.Next(20), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<IReadOnlyList<PlayRecord>> ReadAsync(DateTimeOffset? since = null, CancellationToken cancellationToken = default)
    {
        var byId = new Dictionary<string, PlayRecord>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var file in LogFiles(since))
        {
            string[] lines;
            try
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                lines = (await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Split('\n');
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogUnreadableFile(_logger, Path.GetFileName(file), ex.Message);
                continue;
            }

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0)
                {
                    continue;
                }

                if (TryParse(line) is not { } record)
                {
                    skipped++;
                    continue;
                }

                if (since is { } start && record.Start < start)
                {
                    continue;
                }

                if (!byId.TryGetValue(record.Id, out var known) || record.ListenedSeconds >= known.ListenedSeconds)
                {
                    byId[record.Id] = record;
                }
            }
        }

        if (skipped > 0)
        {
            LogSkippedLines(_logger, skipped);
        }

        return [.. byId.Values.OrderBy(r => r.Start)];
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var file in LogFiles(null))
            {
                File.Delete(file);
            }
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static PlayRecord? TryParse(string line)
    {
        try
        {
            var record = JsonSerializer.Deserialize<PlayRecord>(line, JsonOptions);
            return record is { Id.Length: > 0, ItemId.Length: > 0 } && record.Title is not null && record.ListenedSeconds >= 0 ? record : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private string FileFor(int year) => Path.Combine(Folder, FilePrefix + year.ToString("D4", CultureInfo.InvariantCulture) + FileExtension);

    // Plays are filed by the year they started in, so a period only needs the files from its first year on.
    private IEnumerable<string> LogFiles(DateTimeOffset? since)
    {
        if (!Directory.Exists(Folder))
        {
            return [];
        }

        var fromYear = since?.AddDays(-1).Year ?? int.MinValue;
        return Directory.EnumerateFiles(Folder, FilePrefix + "*" + FileExtension)
            .Where(f => !int.TryParse(Path.GetFileNameWithoutExtension(f).AsSpan(FilePrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var year) || year >= fromYear)
            .Order(StringComparer.OrdinalIgnoreCase);
    }

    // The file is opened for writing by one writer at a time (other copies of the app get a sharing violation and retry),
    // while readers can keep reading. A crash in the middle of a write leaves a last line without its newline: the next
    // line must not be glued to it.
    private static void WriteLine(string path, byte[] line)
    {
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, bufferSize: 1);
        if (stream.Length > 0)
        {
            stream.Seek(-1, SeekOrigin.End);
            if (stream.ReadByte() != '\n')
            {
                stream.WriteByte((byte)'\n');
            }
        }

        stream.Seek(0, SeekOrigin.End);
        stream.Write(line);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the play log {File}: {Reason}")]
    private static partial void LogUnreadableFile(ILogger logger, string file, string reason);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipped {Count} unreadable line(s) of the play log")]
    private static partial void LogSkippedLines(ILogger logger, int count);
}
