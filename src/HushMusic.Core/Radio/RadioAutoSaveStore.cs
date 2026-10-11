using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>One station's "Save songs to a playlist" choice, and the playlist made for it (kept when turned off).</summary>
public sealed record RadioAutoSaveEntry(string StationId, string StationName, bool Enabled, string? PlaylistId);

/// <summary>
/// The per-station "Save songs to a playlist" choices in <c>radio-autosave.json</c> under the app folder, next to the
/// favourite stations. Playlist ids are not secret: only the signed-in account can open its private playlists. Writes go
/// to a temp file that replaces the old one. Thread-safe; <see cref="Changed"/> is raised on the calling thread.
/// </summary>
public sealed class RadioAutoSaveStore(IAppPaths paths, ILogger<RadioAutoSaveStore> logger)
{
    public const string FileName = "radio-autosave.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Dictionary<string, RadioAutoSaveEntry> _entries = new(StringComparer.Ordinal);
    private Task? _load;

    /// <summary>A station was turned on or off (not raised for playlist changes).</summary>
    public event EventHandler? Changed;

    private string FilePath => Path.Combine(paths.Root, FileName);

    public RadioAutoSaveEntry? Get(string stationId)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(stationId);
        }
    }

    public bool IsEnabled(string stationId) => Get(stationId)?.Enabled == true;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _load ??= Task.Run(ReadAsync, CancellationToken.None);
        }

        return load.WaitAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(RadioStation station, bool enabled, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var old = _entries.GetValueOrDefault(station.Id);
            if (old?.Enabled == enabled || (old is null && !enabled))
            {
                return;
            }

            _entries[station.Id] = new RadioAutoSaveEntry(station.Id, station.Name, enabled, old?.PlaylistId);
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await WriteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Remembers the playlist made for a station (null forgets it, so the next song makes a new one).</summary>
    public async Task SetPlaylistAsync(string stationId, string? playlistId, CancellationToken cancellationToken = default)
    {
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_entries.TryGetValue(stationId, out var old) || old.PlaylistId == playlistId)
            {
                return;
            }

            _entries[stationId] = old with { PlaylistId = playlistId };
        }

        await WriteAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The station whose playlist this is, if any (ids compare without the "VL" browse prefix).</summary>
    public string? FindStationByPlaylist(string playlistId)
    {
        ArgumentNullException.ThrowIfNull(playlistId);
        var id = WithoutBrowsePrefix(playlistId);
        lock (_gate)
        {
            return _entries.Values.FirstOrDefault(e => e.PlaylistId is { } p && WithoutBrowsePrefix(p) == id)?.StationId;
        }
    }

    internal static string WithoutBrowsePrefix(string playlistId) =>
        playlistId.StartsWith("VL", StringComparison.Ordinal) ? playlistId[2..] : playlistId;

    private async Task ReadAsync()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var file = await JsonSerializer.DeserializeAsync<AutoSaveFile>(stream, JsonOptions).ConfigureAwait(false);
            var entries = (file?.Stations ?? [])
                .Where(e => e is not null && !string.IsNullOrWhiteSpace(e.StationId))
                .Select(e => e with { StationName = e.StationName ?? string.Empty, PlaylistId = string.IsNullOrWhiteSpace(e.PlaylistId) ? null : e.PlaylistId })
                .DistinctBy(e => e.StationId)
                .ToDictionary(e => e.StationId, StringComparer.Ordinal);
            lock (_gate)
            {
                _entries = entries;
            }

            logger.LogDebug("Loaded the song saving choices of {Count} radio stations", entries.Count);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "The radio song saving file is unreadable; starting with every station off");
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the write gate, so the last write always carries the latest choices.
            List<RadioAutoSaveEntry> entries;
            lock (_gate)
            {
                entries = [.. _entries.Values.OrderBy(e => e.StationName, StringComparer.OrdinalIgnoreCase).ThenBy(e => e.StationId, StringComparer.Ordinal)];
            }

            var path = FilePath;
            var temp = path + ".tmp";
            Directory.CreateDirectory(paths.Root);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new AutoSaveFile(FormatVersion, entries), JsonOptions, CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HushException("Couldn't save your radio settings.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed record AutoSaveFile(int Version, IReadOnlyList<RadioAutoSaveEntry>? Stations);
}
