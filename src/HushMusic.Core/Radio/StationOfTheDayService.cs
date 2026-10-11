using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// <see cref="IStationOfTheDay"/>: picks with <see cref="StationOfTheDayPicker"/> from the favourite stations, the local play
/// log of the last <see cref="ListeningWindow"/> and one Radio Browser query for the day's genre, then keeps the pick and
/// its candidates in <c>radio-station-of-the-day.json</c>, so later visits (and "Another one") need no network. Runs only
/// when asked, i.e. when the Radio page opens.
/// </summary>
public sealed class StationOfTheDayService(
    IRadioDirectory directory,
    IRadioFavorites favorites,
    IPlayLog playLog,
    IAppPaths paths,
    TimeProvider time,
    ILogger<StationOfTheDayService> logger) : IStationOfTheDay
{
    public const string FileName = "radio-station-of-the-day.json";

    /// <summary>How far back listening counts towards the user's genres.</summary>
    public static readonly TimeSpan ListeningWindow = TimeSpan.FromDays(90);

    private const int FormatVersion = 1;
    private const int DirectoryLimit = 40;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        IgnoreReadOnlyProperties = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DayFile? _day;
    private bool _read;

    // A day the directory answered but nothing was left to pick: not asked again that day.
    private DateOnly? _nothingOn;

    private string FilePath => Path.Combine(paths.Root, FileName);

    // Off the calling (UI) thread: the pick reads files and the play log.
    public Task<StationOfTheDay?> GetAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetCoreAsync(cancellationToken), cancellationToken);

    public Task<StationOfTheDay?> RerollAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => RerollCoreAsync(cancellationToken), cancellationToken);

    private async Task<StationOfTheDay?> GetCoreAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var today = Today();
            if (!_read)
            {
                _day = await ReadAsync().ConfigureAwait(false);
                _read = true;
            }

            if (_day?.Day != today && _nothingOn != today)
            {
                _day = await PickAsync(today, cancellationToken).ConfigureAwait(false);
                if (_day is not null)
                {
                    await WriteAsync(_day).ConfigureAwait(false);
                }
            }

            return _day?.Day == today ? ToResult(_day) : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<StationOfTheDay?> RerollCoreAsync(CancellationToken cancellationToken)
    {
        await GetCoreAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_day is not { Rerolled: false } day || day.Day != Today())
            {
                return _day?.Day == Today() ? ToResult(_day) : null;
            }

            var heard = (await ReadListeningAsync(cancellationToken).ConfigureAwait(false)).Select(l => l.StationId).ToHashSet(StringComparer.Ordinal);
            var favoriteIds = favorites.Items.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var candidates = day.Candidates.Where(s => !favoriteIds.Contains(s.Id)).ToList();
            var station = StationOfTheDayPicker.Pick(candidates, day.Day, attempt: 1, heard, skip: day.Station.Id);
            _day = day with { Station = station ?? day.Station, Rerolled = true };
            await WriteAsync(_day).ConfigureAwait(false);
            return ToResult(_day);
        }
        finally
        {
            _gate.Release();
        }
    }

    private DateOnly Today() => DateOnly.FromDateTime(time.GetLocalNow().DateTime);

    private static StationOfTheDay? ToResult(DayFile? day) =>
        day is null ? null : new StationOfTheDay(day.Day, day.Station, day.Label, day.IsFromTaste, CanReroll: !day.Rerolled && day.Candidates.Count > 1);

    private async Task<DayFile?> PickAsync(DateOnly today, CancellationToken cancellationToken)
    {
        await favorites.LoadAsync(cancellationToken).ConfigureAwait(false);
        var favoriteStations = favorites.Items;
        var listened = await ReadListeningAsync(cancellationToken).ConfigureAwait(false);
        var genres = CuratedRadio.Genres;
        var taste = StationOfTheDayPicker.Taste(favoriteStations, listened, genres);
        if (StationOfTheDayPicker.ChooseGenre(taste, genres, today) is not { } genre)
        {
            return null;
        }

        IReadOnlyList<RadioStation>? found = null;
        try
        {
            found = await directory.GetByTagsAsync([genre.Tag], DirectoryLimit, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The curated stations of the genre are enough for a pick.
            logger.LogInformation(ex, "Could not load {Genre} stations for the station of the day", genre.Tag);
        }

        var favoriteIds = favoriteStations.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var heard = listened.Select(l => l.StationId).ToHashSet(StringComparer.Ordinal);
        var candidates = StationOfTheDayPicker.Candidates(genre.Tag, found ?? [], genres, favoriteIds);
        if (StationOfTheDayPicker.Pick(candidates, today, attempt: 0, heard) is not { } station)
        {
            logger.LogInformation("No station of the day: nothing to pick in {Genre}", genre.Tag);
            if (found is not null)
            {
                _nothingOn = today;
            }

            return null;
        }

        logger.LogDebug("Station of the day {Day}: {Station} ({Genre}, from {Count} candidates)", today, station.Name, genre.Tag, candidates.Count);
        return new DayFile(FormatVersion, today, genre.Tag, genre.Label, genre.IsFromTaste, Rerolled: false, station, candidates);
    }

    private async Task<IReadOnlyList<ListenedStation>> ReadListeningAsync(CancellationToken cancellationToken)
    {
        try
        {
            var plays = await playLog.ReadAsync(time.GetLocalNow() - ListeningWindow, cancellationToken).ConfigureAwait(false);
            return StationOfTheDayPicker.Listening(plays);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not read the play log for the station of the day");
            return [];
        }
    }

    private async Task<DayFile?> ReadAsync()
    {
        var path = FilePath;
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var day = await JsonSerializer.DeserializeAsync<DayFile>(stream, JsonOptions).ConfigureAwait(false);
            return day is { Station: not null, Candidates: not null } && !string.IsNullOrWhiteSpace(day.Station.StreamUrl) ? day : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning(ex, "The station of the day file is unreadable; picking again");
            return null;
        }
    }

    private async Task WriteAsync(DayFile day)
    {
        try
        {
            var path = FilePath;
            var temp = path + ".tmp";
            Directory.CreateDirectory(paths.Root);
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer.SerializeAsync(stream, day, JsonOptions, CancellationToken.None).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still today's pick for this session; only a restart could pick again.
            logger.LogWarning(ex, "Could not save the station of the day");
        }
    }

    private sealed record DayFile(
        int Version,
        DateOnly Day,
        string Tag,
        string Label,
        bool IsFromTaste,
        bool Rerolled,
        RadioStation Station,
        IReadOnlyList<RadioStation> Candidates);
}

public static class StationOfTheDayServiceCollectionExtensions
{
    /// <summary>The Radio page's "Station of the day".</summary>
    public static IServiceCollection AddStationOfTheDay(this IServiceCollection services)
    {
        services.AddSingleton<IStationOfTheDay, StationOfTheDayService>();
        return services;
    }
}
