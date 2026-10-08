namespace HushMusic.Core.Models;

/// <summary>
/// A live internet radio station, from the curated list or the Radio Browser directory (radio-browser.info).
/// It plays as a queue item: <see cref="LiveRadio.ToTrack"/> wraps it in a <see cref="Track"/>.
/// </summary>
public sealed record RadioStation
{
    /// <summary>Radio Browser station uuid, or "curated-…" for a curated station that is not in the directory.</summary>
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>The direct audio stream (Radio Browser's url_resolved): MP3 or AAC over HTTP(S).</summary>
    public required string StreamUrl { get; init; }

    public string? Homepage { get; init; }

    public string? LogoUrl { get; init; }

    /// <summary>Genre tags, most relevant first.</summary>
    public IReadOnlyList<string> Tags { get; init; } = [];

    public string? Country { get; init; }

    /// <summary>ISO 3166-1 alpha-2, e.g. "RO".</summary>
    public string? CountryCode { get; init; }

    /// <summary>"MP3", "AAC" or "AAC+".</summary>
    public string? Codec { get; init; }

    public int? BitrateKbps { get; init; }

    /// <summary>True when <see cref="Id"/> is a Radio Browser uuid (plays can be counted there).</summary>
    public bool IsInDirectory => Guid.TryParse(Id, out _);
}

/// <summary>A genre of the curated radio list, with the Radio Browser tags that find more stations like it.</summary>
public sealed record RadioGenre(string Id, string Name, IReadOnlyList<string> DirectoryTags, IReadOnlyList<RadioStation> Stations);

/// <summary>The song a live station is playing, from its ICY "StreamTitle" metadata.</summary>
/// <param name="StationId">The <see cref="RadioStation.Id"/> it belongs to.</param>
/// <param name="StreamTitle">The raw title as sent, usually "Artist - Title".</param>
/// <param name="Artist">The part before " - ", when there is one.</param>
/// <param name="Title">The song title (the whole stream title when it has no artist part).</param>
/// <param name="ArtworkUrl">An image from the ICY "StreamUrl" field (cover or station art), when the station sends one.</param>
public sealed record RadioNowPlaying(string StationId, string StreamTitle, string? Artist, string Title, string? ArtworkUrl);

/// <summary>Live radio stations as queue items.</summary>
public static class LiveRadio
{
    /// <summary>Prefix of <see cref="Track.VideoId"/> for radio stations; such ids never reach YouTube.</summary>
    public const string IdPrefix = "radio:";

    public static bool IsRadioId(string? videoId) => videoId is not null && videoId.StartsWith(IdPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The station as a playable <see cref="Track"/>: title = station name, "artist" = a short description (genres,
    /// country), artwork = the logo, no duration.
    /// </summary>
    public static Track ToTrack(this RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        return new Track
        {
            Title = station.Name,
            VideoId = IdPrefix + station.Id,
            Station = station,
            Artists = [new ArtistRef(Describe(station), null)],
            Thumbnails = string.IsNullOrWhiteSpace(station.LogoUrl) ? [] : [new Thumbnail(station.LogoUrl, 0, 0)],
        };
    }

    /// <summary>"Deep house, nu disco · Romania", or "Live radio" when nothing is known.</summary>
    public static string Describe(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        var tags = string.Join(", ", station.Tags.Take(2).Select(Capitalize));
        var parts = new[] { tags, station.Country }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        return parts.Count == 0 ? "Live radio" : string.Join(" · ", parts);
    }

    /// <summary>What to show as the title of a playing station: the current song, else the station name.</summary>
    public static string DisplayTitle(Track track, RadioNowPlaying? nowPlaying)
    {
        ArgumentNullException.ThrowIfNull(track);
        return nowPlaying is not null && IsFor(track, nowPlaying) ? nowPlaying.Title : track.Title;
    }

    /// <summary>The line under the title: "Artist · Station" while a song is known, else the station description.</summary>
    public static string DisplaySubtitle(Track track, RadioNowPlaying? nowPlaying)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (nowPlaying is null || !IsFor(track, nowPlaying))
        {
            return track.Station is { } station ? Describe(station) : track.ArtistsText;
        }

        return string.IsNullOrWhiteSpace(nowPlaying.Artist) ? track.Title : $"{nowPlaying.Artist} · {track.Title}";
    }

    /// <summary>True when <paramref name="nowPlaying"/> describes the station <paramref name="track"/> plays.</summary>
    public static bool IsFor(Track? track, RadioNowPlaying? nowPlaying) =>
        track?.Station is { } station && nowPlaying is not null && string.Equals(station.Id, nowPlaying.StationId, StringComparison.Ordinal);

    private static string Capitalize(string tag) =>
        tag.Length == 0 ? tag : char.ToUpperInvariant(tag[0]) + tag[1..];
}
