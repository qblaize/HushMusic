using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace HushMusic.Core.Features.LastFm;

/// <summary>
/// Scrobbles waiting to be sent, oldest first, persisted as JSON so they survive restarts and offline periods.
/// Thread-safe. Past <see cref="Capacity"/> the oldest entries are dropped.
/// </summary>
internal sealed class ScrobbleQueue(string filePath, ILogger logger)
{
    public const int Capacity = 2800;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly List<LastFmScrobble> _items = [];

    public string FilePath { get; } = filePath;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(FilePath))
        {
            return;
        }

        List<LastFmScrobble>? loaded;
        try
        {
            await using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
            loaded = await JsonSerializer.DeserializeAsync<List<LastFmScrobble>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "The Last.fm scrobble queue is unreadable; starting empty");
            return;
        }

        var valid = (loaded ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Artist) && !string.IsNullOrWhiteSpace(s.Track) && s.Timestamp > 0);
        lock (_gate)
        {
            _items.InsertRange(0, valid);
            TrimNoLock();
        }
    }

    public void Enqueue(LastFmScrobble scrobble)
    {
        lock (_gate)
        {
            _items.Add(scrobble);
            TrimNoLock();
        }
    }

    /// <summary>The oldest <paramref name="max"/> entries, without removing them.</summary>
    public IReadOnlyList<LastFmScrobble> Peek(int max)
    {
        lock (_gate)
        {
            return [.. _items.Take(max)];
        }
    }

    /// <summary>Removes exactly the given entries (by reference), e.g. a batch returned by <see cref="Peek"/>.</summary>
    public void Remove(IReadOnlyList<LastFmScrobble> sent)
    {
        var set = new HashSet<LastFmScrobble>(sent, ReferenceEqualityComparer.Instance);
        lock (_gate)
        {
            _items.RemoveAll(set.Contains);
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>Writes the current contents atomically (the file is deleted when the queue is empty).</summary>
    public async Task SaveAsync(CancellationToken cancellationToken)
    {
        await _fileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            LastFmScrobble[] snapshot;
            lock (_gate)
            {
                snapshot = [.. _items];
            }

            if (snapshot.Length == 0)
            {
                File.Delete(FilePath);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var temp = FilePath + ".tmp";
            await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken).ConfigureAwait(false);
            }

            File.Move(temp, FilePath, overwrite: true);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private void TrimNoLock()
    {
        if (_items.Count > Capacity)
        {
            _items.RemoveRange(0, _items.Count - Capacity);
        }
    }
}
