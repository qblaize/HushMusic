using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>
/// The Radio Browser directory (radio-browser.info): a free, open list of internet radio stations. Results contain only
/// stations the app can play (MP3/AAC over HTTP(S), working at the directory's last check) and are cached for a while.
/// Implemented in Core.
/// </summary>
public interface IRadioDirectory
{
    /// <summary>The most played stations with any of <paramref name="tags"/> (exact tag match), merged.</summary>
    Task<IReadOnlyList<RadioStation>> GetByTagsAsync(IReadOnlyList<string> tags, int limit, CancellationToken cancellationToken = default);

    /// <summary>Stations whose name contains <paramref name="query"/>, most played first.</summary>
    Task<IReadOnlyList<RadioStation>> SearchAsync(string query, int limit, CancellationToken cancellationToken = default);

    /// <summary>Tells the directory a station was played (its "click" counter, the API's etiquette). Best effort.</summary>
    Task CountClickAsync(string stationId, CancellationToken cancellationToken = default);
}

/// <summary>The user's favourite radio stations, kept in a local file. Thread-safe; <see cref="Changed"/> is raised on the calling thread.</summary>
public interface IRadioFavorites
{
    /// <summary>Most recently added first. Empty until <see cref="LoadAsync"/> has run.</summary>
    IReadOnlyList<RadioStation> Items { get; }

    event EventHandler? Changed;

    bool Contains(string stationId);

    /// <summary>Reads the file once; later calls return at once.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Adds (at the top) or removes the station and saves the file.</summary>
    Task SetAsync(RadioStation station, bool isFavorite, CancellationToken cancellationToken = default);
}

public sealed class RadioNowPlayingChangedEventArgs(string stationId, RadioNowPlaying? nowPlaying) : EventArgs
{
    public string StationId { get; } = stationId;

    /// <summary>Null when the song is no longer known (another station, or the station sends no title).</summary>
    public RadioNowPlaying? NowPlaying { get; } = nowPlaying;
}

/// <summary>
/// "Now playing" song titles of the live station that is playing, read from its Icecast/Shoutcast ICY metadata.
/// The player starts and stops it (<see cref="Follow"/> / <see cref="Unfollow"/>) so it only runs while a station plays.
/// <see cref="Changed"/> is raised on a background thread. Implemented in Core.
/// </summary>
public interface IRadioNowPlaying
{
    /// <summary>The song on the followed station, or null when unknown.</summary>
    RadioNowPlaying? Current { get; }

    event EventHandler<RadioNowPlayingChangedEventArgs>? Changed;

    /// <summary>Starts (or resumes) reading titles for <paramref name="station"/>. A different station clears the old title.</summary>
    void Follow(RadioStation station);

    /// <summary>Stops reading titles. <paramref name="forget"/> also clears the current title (the station is no longer current).</summary>
    void Unfollow(bool forget);
}
