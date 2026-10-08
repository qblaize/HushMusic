using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>
/// Finds the song a live station is playing (its ICY "Artist - Title") on YouTube Music, so it can be played, queued,
/// liked or saved. Implemented in Core on top of <see cref="ISearchApi"/>.
/// </summary>
public interface IRadioTrackMatcher
{
    /// <summary>
    /// The search text for <paramref name="song"/>: "Artist Title" without radio-edit tags, the station's name, web
    /// addresses and decorations. Empty when nothing searchable is left.
    /// </summary>
    string GetQuery(RadioNowPlaying song, string? stationName = null);

    /// <summary>
    /// Up to <paramref name="count"/> YouTube Music songs for <paramref name="song"/>, the closest match first; empty when
    /// nothing matches. Results are remembered for the session. Network errors propagate.
    /// </summary>
    Task<IReadOnlyList<Track>> FindAsync(RadioNowPlaying song, string? stationName = null, int count = 3, CancellationToken cancellationToken = default);
}
