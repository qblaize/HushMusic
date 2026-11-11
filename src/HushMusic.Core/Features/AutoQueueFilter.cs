using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Services;

namespace HushMusic.Core.Features;

/// <summary><see cref="IAutoQueueFilter"/>: smart skip plus the blocklist. Holds no state of its own.</summary>
public sealed class AutoQueueFilter(ISmartSkip? smartSkip = null, IBlocklist? blocklist = null) : IAutoQueueFilter
{
    public event EventHandler? Changed
    {
        add
        {
            if (smartSkip is not null)
            {
                smartSkip.Changed += value;
            }

            if (blocklist is not null)
            {
                blocklist.Changed += value;
            }
        }

        remove
        {
            if (smartSkip is not null)
            {
                smartSkip.Changed -= value;
            }

            if (blocklist is not null)
            {
                blocklist.Changed -= value;
            }
        }
    }

    public bool IsLeftOut(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return smartSkip?.IsLeftOut(track.VideoId) == true || blocklist?.IsLeftOut(track) == true;
    }
}

/// <summary>
/// Which artists a list of <see cref="BlockedArtist"/> stands for, as hash sets built once. An artist with a channel id
/// matches a blocked id, or the name of an artist blocked without one; an artist without an id (e.g. a song matched from
/// live radio) matches any blocked name. Immutable, so it can be read from any thread. Pure.
/// </summary>
public sealed class ArtistMatcher
{
    public static readonly ArtistMatcher Empty = new([]);

    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _names = new(StringComparer.Ordinal);
    private readonly HashSet<string> _namesWithoutId = new(StringComparer.Ordinal);

    public ArtistMatcher(IEnumerable<BlockedArtist> artists)
    {
        ArgumentNullException.ThrowIfNull(artists);
        foreach (var artist in artists)
        {
            var name = NameKey(artist.Name);
            if (HasId(artist.BrowseId))
            {
                _ids.Add(artist.BrowseId!);
            }
            else if (name.Length > 0)
            {
                _namesWithoutId.Add(name);
            }

            if (name.Length > 0)
            {
                _names.Add(name);
            }
        }
    }

    public bool IsEmpty => _ids.Count == 0 && _names.Count == 0;

    public bool IsBlocked(ArtistRef artist)
    {
        ArgumentNullException.ThrowIfNull(artist);
        if (HasId(artist.BrowseId))
        {
            return _ids.Contains(artist.BrowseId!) || (_namesWithoutId.Count > 0 && _namesWithoutId.Contains(NameKey(artist.Name)));
        }

        return _names.Count > 0 && _names.Contains(NameKey(artist.Name));
    }

    /// <summary>One of the song's artists is blocked.</summary>
    public bool IsBlocked(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        if (IsEmpty)
        {
            return false;
        }

        foreach (var artist in track.Artists)
        {
            if (IsBlocked(artist))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The entry stands for the artist: the same channel id when both have one, otherwise the same name.</summary>
    public static bool Matches(BlockedArtist entry, ArtistRef artist)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(artist);
        if (HasId(entry.BrowseId) && HasId(artist.BrowseId))
        {
            return string.Equals(entry.BrowseId, artist.BrowseId, StringComparison.Ordinal);
        }

        var name = NameKey(entry.Name);
        return name.Length > 0 && name == NameKey(artist.Name);
    }

    /// <summary>An artist name folded for matching: case, accents and spacing aside ("Beyoncé " → "beyonce").</summary>
    public static string NameKey(string? name) => string.Join(' ', TrackListView.Terms(name));

    private static bool HasId(string? id) => !string.IsNullOrWhiteSpace(id);
}
