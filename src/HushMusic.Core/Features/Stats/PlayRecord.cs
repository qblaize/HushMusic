using System.Text.Json.Serialization;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features.Stats;

public enum PlayKind
{
    /// <summary>A YouTube Music song, video or episode.</summary>
    Track,

    /// <summary>A live internet radio station.</summary>
    Radio,
}

/// <summary>An artist credit as stored in the play log.</summary>
public sealed record PlayArtist(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("id")] string? BrowseId);

/// <summary>
/// One line of the local play log (<c>history\plays-YYYY.jsonl</c>). A play is written when it starts to count and again
/// when it ends (long ones also every few minutes), always with the same <see cref="Id"/>: readers keep the line with the
/// most listening time.
/// </summary>
public sealed record PlayRecord
{
    public const int CurrentVersion = 1;

    [JsonPropertyName("v")]
    public int Version { get; init; } = CurrentVersion;

    /// <summary>Identifies one play across its lines.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("kind")]
    public PlayKind Kind { get; init; }

    /// <summary>The video id, or the station id for radio.</summary>
    [JsonPropertyName("item")]
    public required string ItemId { get; init; }

    /// <summary>The song title, or the station name.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("artists")]
    public IReadOnlyList<PlayArtist> Artists { get; init; } = [];

    [JsonPropertyName("album")]
    public string? Album { get; init; }

    [JsonPropertyName("albumId")]
    public string? AlbumId { get; init; }

    /// <summary>Cover art, or the station logo.</summary>
    [JsonPropertyName("art")]
    public string? ArtUrl { get; init; }

    /// <summary>When it started, in the local time of that moment (the offset is kept).</summary>
    [JsonPropertyName("start")]
    public DateTimeOffset Start { get; init; }

    /// <summary>Seconds of actual playback (pauses, buffering and skipped parts don't count).</summary>
    [JsonPropertyName("listened")]
    public int ListenedSeconds { get; init; }

    /// <summary>The track's length in seconds, when known (never for radio).</summary>
    [JsonPropertyName("duration")]
    public int? DurationSeconds { get; init; }

    [JsonIgnore]
    public TimeSpan Listened => TimeSpan.FromSeconds(ListenedSeconds);

    [JsonIgnore]
    public TimeSpan? Duration => DurationSeconds is { } seconds && seconds > 0 ? TimeSpan.FromSeconds(seconds) : null;

    /// <summary>True when this play counts as a play (see <see cref="PlayRules.CountsAsPlay"/>).</summary>
    [JsonIgnore]
    public bool IsPlay => PlayRules.CountsAsPlay(Listened, Duration);

    /// <summary>A record for <paramref name="track"/> (a song or a radio station) that started at <paramref name="start"/>.</summary>
    public static PlayRecord For(string id, Track track, DateTimeOffset start, TimeSpan listened, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(track);
        var seconds = (int)Math.Round(listened.TotalSeconds, MidpointRounding.AwayFromZero);
        if (track.Station is { } station)
        {
            return new PlayRecord
            {
                Id = id,
                Kind = PlayKind.Radio,
                ItemId = station.Id,
                Title = station.Name,
                ArtUrl = NullIfEmpty(station.LogoUrl),
                Start = start,
                ListenedSeconds = seconds,
            };
        }

        return new PlayRecord
        {
            Id = id,
            Kind = PlayKind.Track,
            ItemId = track.VideoId,
            Title = track.Title,
            Artists = [.. track.Artists.Where(a => !string.IsNullOrWhiteSpace(a.Name)).Select(a => new PlayArtist(a.Name.Trim(), NullIfEmpty(a.BrowseId)))],
            Album = NullIfEmpty(track.Album?.Name?.Trim()),
            AlbumId = NullIfEmpty(track.Album?.BrowseId),
            ArtUrl = NullIfEmpty(track.ThumbnailFor(120)?.Url),
            Start = start,
            ListenedSeconds = seconds,
            DurationSeconds = duration is { } length && length > TimeSpan.Zero ? (int)Math.Round(length.TotalSeconds, MidpointRounding.AwayFromZero) : null,
        };
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>When listening counts as a play.</summary>
public static class PlayRules
{
    /// <summary>A play counts after 30 seconds, or half the track when that is shorter.</summary>
    public static readonly TimeSpan PlayThreshold = TimeSpan.FromSeconds(30);

    /// <summary>Shorter listens (quick skips) aren't logged at all.</summary>
    public static readonly TimeSpan MinimumLogged = TimeSpan.FromSeconds(10);

    public static TimeSpan Threshold(TimeSpan? duration) =>
        duration is { } length && length > TimeSpan.Zero && length / 2 < PlayThreshold ? length / 2 : PlayThreshold;

    public static bool CountsAsPlay(TimeSpan listened, TimeSpan? duration) => listened > TimeSpan.Zero && listened >= Threshold(duration);
}
