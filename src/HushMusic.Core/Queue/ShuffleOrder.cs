namespace HushMusic.Core.Queue;

/// <summary>
/// The shuffle order: random, but each artist's songs are spread over the whole queue, and two songs by the same artist
/// never play back to back while there is anyone else to put between them.
/// </summary>
/// <remarks>
/// Each artist's songs are shuffled and laid out evenly along the queue from a random starting offset (with a little
/// jitter, so artists with the same number of songs don't alternate in lockstep). Merging every artist's positions
/// gives the order; a last pass separates any two songs by the same artist that still meet.
/// </remarks>
public static class ShuffleOrder
{
    // How far a song may move from its even spot, as a share of the gap between that artist's songs.
    private const double Jitter = 0.2;

    /// <summary>A new shuffled order of <paramref name="items"/>.</summary>
    /// <param name="artist">The item's artist; null or empty when unknown (such items are placed freely).</param>
    /// <param name="previousArtist">The artist playing right before the first item (the current song), or null.</param>
    public static List<T> Spread<T>(IReadOnlyList<T> items, Func<T, string?> artist, Random random, string? previousArtist = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(random);

        var groups = new Dictionary<string, List<T>>(StringComparer.Ordinal);
        var placed = new List<(double Position, T Item)>(items.Count);
        foreach (var item in items)
        {
            if (Key(artist(item)) is not { } key)
            {
                placed.Add((random.NextDouble(), item));
                continue;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                groups[key] = group = [];
            }

            group.Add(item);
        }

        foreach (var group in groups.Values)
        {
            var songs = group.ToArray();
            random.Shuffle(songs);
            var gap = 1.0 / songs.Length;
            var offset = random.NextDouble() * gap;
            for (var i = 0; i < songs.Length; i++)
            {
                var jitter = (random.NextDouble() - 0.5) * gap * Jitter;
                placed.Add((offset + (i * gap) + jitter, songs[i]));
            }
        }

        var order = placed.OrderBy(p => p.Position).Select(p => p.Item).ToList();
        SeparateNeighbours(order, item => Key(artist(item)), Key(previousArtist));
        return order;
    }

    // Wherever a song follows one by the same artist, the next song by someone else moves up in front of it. When only
    // that artist is left, the song moves back instead, between two songs by others.
    private static void SeparateNeighbours<T>(List<T> order, Func<T, string?> key, string? first)
    {
        var previous = first;
        for (var i = 0; i < order.Count; i++)
        {
            var current = key(order[i]);
            if (current is null || current != previous)
            {
                previous = current;
                continue;
            }

            var other = order.FindIndex(i + 1, item => key(item) != current);
            if (other >= 0)
            {
                var item = order[other];
                order.RemoveAt(other);
                order.Insert(i, item);
                previous = key(item);
                continue;
            }

            var slot = EarlierSlot(order, i, current, key, first);
            if (slot < 0)
            {
                // Nowhere left to put it.
                return;
            }

            var song = order[i];
            order.RemoveAt(i);
            order.Insert(slot, song);
        }
    }

    // The latest position before `index` where a song by `artist` would sit between two songs by others.
    private static int EarlierSlot<T>(List<T> order, int index, string artist, Func<T, string?> key, string? first)
    {
        for (var slot = index - 1; slot >= 0; slot--)
        {
            var before = slot == 0 ? first : key(order[slot - 1]);
            if (before != artist && key(order[slot]) != artist)
            {
                return slot;
            }
        }

        return -1;
    }

    private static string? Key(string? artist) =>
        string.IsNullOrWhiteSpace(artist) ? null : artist.Trim().ToUpperInvariant();
}
