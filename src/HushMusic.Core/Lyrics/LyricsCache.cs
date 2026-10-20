using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace HushMusic.Core.Lyrics;

/// <summary>What one source answered for one track, and when.</summary>
internal sealed record LyricsProviderResult(DateTimeOffset FetchedAt, IReadOnlyList<LyricsDocument> Documents)
{
    public LyricsDocument? Find(string sourceKey) => Documents.FirstOrDefault(d => d.SourceKey == sourceKey);
}

/// <summary>
/// Answers per track and source: the last <see cref="MemoryCapacity"/> tracks in memory, and one small JSON file per
/// track under cache\lyrics. Lyrics are kept for <see cref="FoundLifetime"/>, a "no lyrics" answer for
/// <see cref="MissingLifetime"/>; the folder is pruned to <see cref="MaxFiles"/> files. Disk errors are logged, never
/// thrown. Thread-safe.
/// </summary>
internal sealed partial class LyricsCache(string folder, TimeProvider time, ILogger logger)
{
    public const int MemoryCapacity = 50;

    public const int MaxFiles = 1000;

    public static readonly TimeSpan FoundLifetime = TimeSpan.FromDays(7);

    public static readonly TimeSpan MissingLifetime = TimeSpan.FromHours(1);

    private const int FileVersion = 1;
    private const int PruneEveryWrites = 100;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _memory = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recent = new();
    private readonly SemaphoreSlim _disk = new(1, 1);
    private int _writesUntilPrune;

    /// <summary>The fresh answer of <paramref name="provider"/> for the track, or null.</summary>
    public async Task<LyricsProviderResult?> GetAsync(string videoId, LyricsProviderId provider, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(videoId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return entry.Results.TryGetValue(provider, out var result) && IsFresh(result) ? result : null;
        }
    }

    /// <summary>Every fresh answer for the track.</summary>
    public async Task<IReadOnlyDictionary<LyricsProviderId, LyricsProviderResult>> GetAllAsync(string videoId, CancellationToken cancellationToken)
    {
        var entry = await GetEntryAsync(videoId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return entry.Results.Where(r => IsFresh(r.Value)).ToDictionary(r => r.Key, r => r.Value);
        }
    }

    public async Task StoreAsync(string videoId, LyricsProviderId provider, LyricsProviderResult result)
    {
        var entry = await GetEntryAsync(videoId, CancellationToken.None).ConfigureAwait(false);
        LyricsCacheFile file;
        lock (_gate)
        {
            entry.Results[provider] = result;
            file = new LyricsCacheFile
            {
                Version = FileVersion,
                Results = entry.Results
                    .Where(r => IsFresh(r.Value))
                    .ToDictionary(r => r.Key.ToString(), r => CachedResult.From(r.Value)),
            };
        }

        if (PathFor(videoId) is not { } path)
        {
            return;
        }

        await _disk.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, file, LyricsCacheJson.Default.LyricsCacheFile).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            if (--_writesUntilPrune <= 0)
            {
                _writesUntilPrune = PruneEveryWrites;
                Prune();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogDiskFailure(logger, videoId, ex);
        }
        finally
        {
            _disk.Release();
        }
    }

    private bool IsFresh(LyricsProviderResult result) =>
        time.GetUtcNow() - result.FetchedAt < (result.Documents.Count > 0 ? FoundLifetime : MissingLifetime);

    private async Task<Entry> GetEntryAsync(string videoId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_memory.TryGetValue(videoId, out var node))
            {
                _recent.Remove(node);
                _recent.AddFirst(node);
                return node.Value;
            }
        }

        var loaded = await ReadAsync(videoId, cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (_memory.TryGetValue(videoId, out var raced))
            {
                return raced.Value;
            }

            var entry = new Entry(videoId);
            foreach (var (key, cached) in loaded?.Results ?? [])
            {
                if (Enum.TryParse<LyricsProviderId>(key, out var provider) && cached.ToResult() is { } result && IsFresh(result))
                {
                    entry.Results[provider] = result;
                }
            }

            _memory[videoId] = _recent.AddFirst(entry);
            while (_recent.Count > MemoryCapacity)
            {
                _memory.Remove(_recent.Last!.Value.VideoId);
                _recent.RemoveLast();
            }

            return entry;
        }
    }

    // Callers may be on the UI thread: the file system is only touched on the thread pool.
    private Task<LyricsCacheFile?> ReadAsync(string videoId, CancellationToken cancellationToken) => PathFor(videoId) is not { } path
        ? Task.FromResult<LyricsCacheFile?>(null)
        : Task.Run<LyricsCacheFile?>(
            async () =>
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
                    var file = await JsonSerializer.DeserializeAsync(stream, LyricsCacheJson.Default.LyricsCacheFile, cancellationToken).ConfigureAwait(false);
                    return file?.Version == FileVersion ? file : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    LogDiskFailure(logger, videoId, ex);
                    return null;
                }
            },
            cancellationToken);

    // Video ids are [A-Za-z0-9_-]; anything else stays in memory only.
    private string? PathFor(string videoId)
    {
        if (videoId.Length is 0 or > 64 || !videoId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return null;
        }

        return Path.Combine(folder, videoId + ".json");
    }

    /// <summary>Deletes files past their lifetime, then the oldest ones beyond <see cref="MaxFiles"/>.</summary>
    private void Prune()
    {
        var files = new DirectoryInfo(folder).GetFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
        var cutoff = time.GetUtcNow().UtcDateTime - FoundLifetime;
        for (var i = 0; i < files.Count; i++)
        {
            if (i >= MaxFiles || files[i].LastWriteTimeUtc < cutoff)
            {
                try
                {
                    files[i].Delete();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogDiskFailure(logger, files[i].Name, ex);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Lyrics cache file for {VideoId} could not be read or written")]
    private static partial void LogDiskFailure(ILogger logger, string videoId, Exception exception);

    private sealed class Entry(string videoId)
    {
        public string VideoId { get; } = videoId;

        public Dictionary<LyricsProviderId, LyricsProviderResult> Results { get; } = [];
    }
}

internal sealed class LyricsCacheFile
{
    [JsonPropertyName("v")]
    public int Version { get; set; }

    [JsonPropertyName("results")]
    public Dictionary<string, CachedResult> Results { get; set; } = [];
}

internal sealed class CachedResult
{
    [JsonPropertyName("at")]
    public DateTimeOffset FetchedAt { get; set; }

    [JsonPropertyName("docs")]
    public List<CachedDocument> Documents { get; set; } = [];

    public static CachedResult From(LyricsProviderResult result) => new()
    {
        FetchedAt = result.FetchedAt,
        Documents = [.. result.Documents.Select(CachedDocument.From)],
    };

    /// <summary>Null when the file names a source this version doesn't know.</summary>
    public LyricsProviderResult? ToResult()
    {
        var documents = new List<LyricsDocument>(Documents.Count);
        foreach (var cached in Documents)
        {
            if (cached.ToDocument() is not { } document)
            {
                return null;
            }

            documents.Add(document);
        }

        return new LyricsProviderResult(FetchedAt, documents);
    }
}

internal sealed class CachedDocument
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("sync")]
    [JsonConverter(typeof(JsonStringEnumConverter<LyricsSyncLevel>))]
    public LyricsSyncLevel Sync { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("instrumental")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Instrumental { get; set; }

    [JsonPropertyName("writers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Writers { get; set; }

    [JsonPropertyName("attribution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Attribution { get; set; }

    [JsonPropertyName("lines")]
    public List<CachedLine> Lines { get; set; } = [];

    public static CachedDocument From(LyricsDocument document) => new()
    {
        Key = document.SourceKey,
        Name = document.SourceName,
        Sync = document.Sync,
        Url = document.SourceUrl,
        Instrumental = document.IsInstrumental,
        Writers = document.Writers,
        Attribution = document.Attribution,
        Lines = [.. document.Lines.Select(CachedLine.From)],
    };

    public LyricsDocument? ToDocument() => LyricsSources.Find(Key) is null
        ? null
        : new LyricsDocument(Key, Name, Sync, [.. Lines.Select(l => l.ToLine())])
        {
            SourceUrl = Url,
            IsInstrumental = Instrumental,
            Writers = Writers,
            Attribution = Attribution,
        };
}

/// <summary>A line as milliseconds and text, to keep the files small.</summary>
internal sealed class CachedLine
{
    [JsonPropertyName("s")]
    public long Start { get; set; }

    [JsonPropertyName("e")]
    public long End { get; set; }

    [JsonPropertyName("t")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("bg")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Background { get; set; }

    [JsonPropertyName("w")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<CachedWord>? Words { get; set; }

    public static CachedLine From(LyricLine line) => new()
    {
        Start = (long)line.Start.TotalMilliseconds,
        End = (long)line.End.TotalMilliseconds,
        Text = line.Text,
        Background = line.IsBackground,
        Words = line.Words.Count == 0 ? null : [.. line.Words.Select(CachedWord.From)],
    };

    public LyricLine ToLine() => new(
        TimeSpan.FromMilliseconds(Start),
        TimeSpan.FromMilliseconds(End),
        Text,
        Words is null ? [] : [.. Words.Select(w => w.ToWord())])
    {
        IsBackground = Background,
    };
}

internal sealed class CachedWord
{
    [JsonPropertyName("s")]
    public long Start { get; set; }

    [JsonPropertyName("e")]
    public long End { get; set; }

    [JsonPropertyName("t")]
    public string Text { get; set; } = string.Empty;

    public static CachedWord From(LyricWord word) => new()
    {
        Start = (long)word.Start.TotalMilliseconds,
        End = (long)word.End.TotalMilliseconds,
        Text = word.Text,
    };

    public LyricWord ToWord() => new(TimeSpan.FromMilliseconds(Start), TimeSpan.FromMilliseconds(End), Text);
}

[JsonSerializable(typeof(LyricsCacheFile))]
internal sealed partial class LyricsCacheJson : JsonSerializerContext;
