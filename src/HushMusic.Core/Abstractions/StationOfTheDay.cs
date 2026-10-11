using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>The day's station on the Radio page.</summary>
/// <param name="Day">The local date it was picked for.</param>
/// <param name="Genre">The genre it was picked from, for display ("Deep House", "Lounge").</param>
/// <param name="IsFromTaste">Picked from the user's favourite or most played genres; false for a curated genre (no history yet).</param>
/// <param name="CanReroll">"Another one" is still available today.</param>
public sealed record StationOfTheDay(DateOnly Day, RadioStation Station, string Genre, bool IsFromTaste, bool CanReroll);

/// <summary>
/// "Station of the day": one station a day from the genres of the user's favourite and most played stations (curated
/// genres until there are some), not one of their favourites. Picked on first use each day and kept in the radio data,
/// so it stays the same all day. At most one directory query a day. Implemented in Core.
/// </summary>
public interface IStationOfTheDay
{
    /// <summary>Today's station, picked now if needed; null when there is nothing to pick from.</summary>
    Task<StationOfTheDay?> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Picks another station from today's genre, once a day. Returns today's station either way.</summary>
    Task<StationOfTheDay?> RerollAsync(CancellationToken cancellationToken = default);
}
