using HushMusic.Core.Models;

namespace HushMusic.Core.Abstractions;

/// <summary>What a sidebar pin opens.</summary>
public enum PinKind
{
    Playlist,
    Album,
    Artist,

    /// <summary>A live radio station: it has no page, so the pin plays it.</summary>
    Station,
}

/// <summary>
/// A playlist, album, artist or radio station pinned to the sidebar. The title and thumbnail are kept with it, so the
/// sidebar shows it right away, without the network.
/// </summary>
public sealed record SidebarPin
{
    /// <summary>What the pin opens.</summary>
    public required PinKind Kind { get; init; }

    /// <summary>Playlist id (without the "VL" browse prefix), album browse id, artist channel id or station id.</summary>
    public required string Id { get; init; }

    public required string Title { get; init; }

    public string? ThumbnailUrl { get; init; }

    /// <summary>Station pins keep the whole station, so it plays even when the directory is unreachable.</summary>
    public RadioStation? Station { get; init; }

    /// <summary>The station as a pin, or null when it can't be one.</summary>
    public static SidebarPin? For(RadioStation? station) =>
        station is null || string.IsNullOrWhiteSpace(station.Id) || string.IsNullOrWhiteSpace(station.Name)
            ? null
            : new SidebarPin { Kind = PinKind.Station, Id = station.Id, Title = station.Name, ThumbnailUrl = station.LogoUrl, Station = station };

    /// <summary>The id pins are compared by: playlists with or without their "VL" browse prefix are the same.</summary>
    public static string NormalizeId(PinKind kind, string id) =>
        kind == PinKind.Playlist && id.StartsWith("VL", StringComparison.Ordinal) && id.Length > 2 ? id[2..] : id;

    /// <summary>True when this pin is the one for <paramref name="kind"/> and <paramref name="id"/>.</summary>
    public bool Is(PinKind kind, string? id) =>
        Kind == kind && id is not null && string.Equals(NormalizeId(Kind, Id), NormalizeId(kind, id), StringComparison.Ordinal);
}

/// <summary>
/// The items pinned to the sidebar, in the user's order, kept in a local file. Thread-safe; <see cref="Changed"/> is
/// raised on the calling thread (on a background thread after the first load).
/// </summary>
public interface ISidebarPins
{
    /// <summary>The most pins the sidebar keeps.</summary>
    const int MaxCount = 20;

    /// <summary>In sidebar order. Empty until <see cref="LoadAsync"/> has run.</summary>
    IReadOnlyList<SidebarPin> Items { get; }

    event EventHandler? Changed;

    bool Contains(PinKind kind, string id);

    /// <summary>Reads the file once; later calls return at once.</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds the pin at the end and saves. Returns false when the sidebar already has <see cref="MaxCount"/> pins
    /// (nothing changes); a pin that is already there stays where it is and counts as added.
    /// </summary>
    Task<bool> PinAsync(SidebarPin pin, CancellationToken cancellationToken = default);

    Task UnpinAsync(PinKind kind, string id, CancellationToken cancellationToken = default);

    /// <summary>Moves the pin <paramref name="offset"/> places (negative: up), within the list.</summary>
    Task MoveAsync(PinKind kind, string id, int offset, CancellationToken cancellationToken = default);

    /// <summary>
    /// When the item is pinned, takes its current title and thumbnail (a renamed playlist, new art). Does nothing for
    /// items that aren't pinned or haven't changed.
    /// </summary>
    Task RefreshAsync(SidebarPin pin, CancellationToken cancellationToken = default);
}
