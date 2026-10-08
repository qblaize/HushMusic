using System.Text.Json;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// The hand-picked starter stations per genre, embedded as <c>CuratedStations.json</c>. Every stream and logo in it was
/// checked live (HTTP 200, audio content type, data flowing, ICY titles where the station sends them) on 2026-10-08.
/// Stations that are in the directory keep their Radio Browser uuid as id.
/// </summary>
public static class CuratedRadio
{
    private const string ResourceName = "HushMusic.Core.Radio.CuratedStations.json";

    private static readonly Lazy<IReadOnlyList<RadioGenre>> s_genres = new(Load);

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<RadioGenre> Genres => s_genres.Value;

    /// <summary>The curated station with this id, if any.</summary>
    public static RadioStation? Find(string stationId) =>
        Genres.SelectMany(g => g.Stations).FirstOrDefault(s => s.Id == stationId);

    /// <summary>Parses the curated list format (also used by tests).</summary>
    public static IReadOnlyList<RadioGenre> Parse(Stream json)
    {
        var file = JsonSerializer.Deserialize<CuratedFile>(json, JsonOptions) ?? throw new InvalidDataException("The curated station list is empty.");
        return [.. file.Genres.Select(g => new RadioGenre(
            g.Id,
            g.Name,
            g.Tags ?? [],
            [.. g.Stations.Select(s => new RadioStation
            {
                Id = s.Id,
                Name = s.Name,
                StreamUrl = s.StreamUrl,
                Homepage = s.Homepage,
                LogoUrl = s.LogoUrl,
                Tags = s.Tags ?? [],
                Country = s.Country,
                CountryCode = s.CountryCode,
                Codec = s.Codec,
                BitrateKbps = s.Bitrate is > 0 ? s.Bitrate : null,
            })]))];
    }

    private static IReadOnlyList<RadioGenre> Load()
    {
        using var stream = typeof(CuratedRadio).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Missing embedded resource {ResourceName}.");
        return Parse(stream);
    }

    private sealed record CuratedFile(IReadOnlyList<CuratedGenre> Genres);

    private sealed record CuratedGenre(string Id, string Name, IReadOnlyList<string>? Tags, IReadOnlyList<CuratedStation> Stations);

    private sealed record CuratedStation(
        string Id,
        string Name,
        string StreamUrl,
        string? Homepage,
        string? LogoUrl,
        IReadOnlyList<string>? Tags,
        string? Country,
        string? CountryCode,
        string? Codec,
        int? Bitrate);
}
