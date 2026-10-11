using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

public sealed class RadioSongSavedEventArgs(string stationId, RadioNowPlaying song, Track track, string playlistId) : EventArgs
{
    public string StationId { get; } = stationId;

    /// <summary>The song as the station announced it.</summary>
    public RadioNowPlaying Song { get; } = song;

    /// <summary>The YouTube Music song it was matched to.</summary>
    public Track Track { get; } = track;

    public string PlaylistId { get; } = playlistId;
}

/// <summary>
/// "Save songs to a playlist" for live stations: songs heard on a station the user turned it on for go to a private
/// "Heard on &lt;station&gt;" playlist on their YouTube Music account, once they have played for a while and were matched
/// with confidence. Off by default, chosen per station and kept in the radio data. Needs a signed-in account; while signed
/// out nothing is attempted. <see cref="Changed"/> is raised on the calling thread, <see cref="SongSaved"/> on a background
/// thread. Implemented in Core.
/// </summary>
public interface IRadioAutoSave
{
    /// <summary>A station was turned on or off.</summary>
    event EventHandler? Changed;

    /// <summary>A song heard on a station was added to its playlist (or found already there).</summary>
    event EventHandler<RadioSongSavedEventArgs>? SongSaved;

    /// <summary>False until <see cref="LoadAsync"/> has run.</summary>
    bool IsEnabled(string stationId);

    /// <summary>Reads the stored choices once; later calls return at once.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Turns saving on or off for <paramref name="station"/> and stores the choice. Its playlist is kept either way.</summary>
    Task SetEnabledAsync(RadioStation station, bool enabled, CancellationToken cancellationToken = default);

    /// <summary>True when this session saved the song with this stream title, heard on the station.</summary>
    bool WasSaved(string stationId, string streamTitle);
}
