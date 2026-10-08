using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// <see cref="IRadioFavorites"/> in <c>radio-favorites.json</c> under the app folder. Whole stations are stored, so a
/// favourite plays even when the directory is unreachable. Writes go to a temp file that replaces the old one.
/// Read in the background at startup.
/// </summary>
public sealed class RadioFavoritesStore(IAppPaths paths, ILogger<RadioFavoritesStore> logger) : IRadioFavorites, IHostedService
{
    public const string FileName = "radio-favorites.json";
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private List<RadioStation> _items = [];
    private Task? _load;

    public event EventHandler? Changed;

    public IReadOnlyList<RadioStation> Items
    {
        get
        {
            lock (_gate)
            {
                return [.. _items];
            }
        }
    }

    private string FilePath => Path.Combine(paths.Root, FileName);

    public bool Contains(string stationId)
    {
        lock (_gate)
        {
            return _items.Exists(s => s.Id == stationId);
        }
    }

    Task IHostedService.StartAsync(CancellationToken cancellationToken)
    {
        _ = LoadAsync(CancellationToken.None);
        return Task.CompletedTask;
    }

    Task IHostedService.StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task LoadAsync(CancellationToken cancellationToken = default)
    {
        Task load;
        lock (_gate)
        {
            load = _load ??= Task.Run(ReadAsync, CancellationToken.None);
        }

        return load.WaitAsync(cancellationToken);
    }

    public async Task SetAsync(RadioStation station, bool isFavorite, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(station);
        await LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            var index = _items.FindIndex(s => s.Id == station.Id);
            if (isFavorite == index >= 0)
            {
                return;
            }

            if (isFavorite)
            {
                _items.Insert(0, station);
            }
            else
            {
                _items.RemoveAt(index);
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
        await WriteAsync(cancellationToken).ConfigureAwait(false);
    }

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
            var file = await JsonSerializer.DeserializeAsync<FavoritesFile>(stream, JsonOptions).ConfigureAwait(false);
            var stations = file?.Stations?
                .Where(s => s is not null && !string.IsNullOrWhiteSpace(s.Id) && !string.IsNullOrWhiteSpace(s.StreamUrl))
                .DistinctBy(s => s.Id)
                .ToList() ?? [];
            lock (_gate)
            {
                _items = stations;
            }

            logger.LogDebug("Loaded {Count} favourite radio stations", stations.Count);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "The favourite radio stations file is unreadable; starting with none");
            return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task WriteAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read under the write gate, so the last write always carries the latest list.
            var stations = Items;
            var path = FilePath;
            var temp = path + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, new FavoritesFile(FormatVersion, stations), JsonOptions, CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HushException("Couldn't save your favourite stations.", ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private sealed record FavoritesFile(int Version, IReadOnlyList<RadioStation>? Stations);
}
