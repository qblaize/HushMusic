using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>A song the user keeps skipping, as Settings lists it.</summary>
/// <param name="Skips">Quick skips since the song was last listened to (at least half of it).</param>
public sealed record SkippedSong(string VideoId, string Title, string Artists, string? ArtUrl, int Skips, DateTimeOffset LastSkipped);

/// <summary>
/// Smart skip (<see cref="AppSettings.SmartSkip"/>): a song the user skips in its first seconds three times, without
/// listening to it in between, is left out of the songs Hush adds to the queue by itself: radio and Up next top-ups,
/// "keep playing when the queue ends", a radio started from a song (except that song) and the weekly mix. Songs the user
/// picks, and the albums and playlists they play, are never filtered. Kept on this PC only; it isn't a dislike and nothing
/// is sent to YouTube Music. Thread-safe; <see cref="Changed"/> is raised on a background thread. Implemented in Core.
/// </summary>
public interface ISmartSkip
{
    /// <summary>The often-skipped songs, most recently skipped first (also while smart skip is off).</summary>
    IReadOnlyList<SkippedSong> OftenSkipped { get; }

    /// <summary>
    /// Raised when <see cref="OftenSkipped"/> or what <see cref="IsLeftOut"/> answers changed (also when smart skip is
    /// turned on or off).
    /// </summary>
    event EventHandler? Changed;

    /// <summary>True when the song must not be added automatically: smart skip is on and the song is often skipped.</summary>
    bool IsLeftOut(string videoId);

    /// <summary>Forgets the song's skips, so it can be added automatically again.</summary>
    Task ForgiveAsync(string videoId, CancellationToken cancellationToken = default);

    /// <summary>Completes once the saved skips are loaded (they load in the background at startup).</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}

public static class SmartSkipExtensions
{
    /// <summary>The tracks that may be added automatically, in their order (all of them without smart skip).</summary>
    public static List<Track> WithoutLeftOut(this ISmartSkip? smartSkip, IEnumerable<Track> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return smartSkip is null ? [.. tracks] : [.. tracks.Where(t => !smartSkip.IsLeftOut(t.VideoId))];
    }
}
