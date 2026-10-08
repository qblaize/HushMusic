using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// Finds a radio song on YouTube Music (<see cref="IRadioTrackMatcher"/>): a songs-only search for the cleaned
/// "Artist Title" (<see cref="RadioSongQuery"/>), falling back to the unfiltered search (videos, remixes uploaded as
/// videos) when no song matches. YouTube's order is kept, except that results whose title and artist match what the
/// station announced move to the front. Results are cached for the session, so reopening a match is instant.
/// </summary>
public sealed class RadioTrackMatcher(ISearchApi search, ILogger<RadioTrackMatcher> logger) : IRadioTrackMatcher
{
    private const int CacheSize = 64;

    // Only the first results are re-ranked: further down, a loose name match is more likely a cover or a karaoke version.
    private const int RankedCandidates = 10;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<Track>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _cacheOrder = new();

    public string GetQuery(RadioNowPlaying song, string? stationName = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        return RadioSongQuery.Build(song.Artist, song.Title, stationName);
    }

    public async Task<IReadOnlyList<Track>> FindAsync(RadioNowPlaying song, string? stationName = null, int count = 3, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var query = GetQuery(song, stationName);
        if (query.Length == 0)
        {
            return [];
        }

        IReadOnlyList<Track>? cached;
        lock (_gate)
        {
            _cache.TryGetValue(query, out cached);
        }

        if (cached is null)
        {
            var results = await search.SearchAsync(query, SearchFilter.Songs, null, cancellationToken).ConfigureAwait(false);
            var candidates = Candidates(results);
            if (candidates.Count == 0)
            {
                logger.LogDebug("No song matches \"{Query}\"; trying all results", query);
                results = await search.SearchAsync(query, SearchFilter.All, null, cancellationToken).ConfigureAwait(false);
                candidates = Candidates(results);
            }

            cached = Rank(candidates, song, stationName);
            Remember(query, cached);
        }

        return cached.Count <= count ? cached : [.. cached.Take(count)];
    }

    private static List<Track> Candidates(SearchResults results)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var tracks = new List<Track>();
        IEnumerable<MediaItem> items = results.TopResult is { } top ? [top, .. results.AllItems] : results.AllItems;
        foreach (var item in items)
        {
            if (item is Track { IsAvailable: true, IsLiveRadio: false } track && !string.IsNullOrEmpty(track.VideoId) && seen.Add(track.VideoId))
            {
                tracks.Add(track);
            }
        }

        return tracks;
    }

    private static List<Track> Rank(List<Track> candidates, RadioNowPlaying song, string? stationName)
    {
        var title = RadioSongQuery.Comparable(RadioSongQuery.Clean(song.Title, stationName));
        var artist = song.Artist is null || RadioSongQuery.IsPlaceholderArtist(song.Artist)
            ? string.Empty
            : RadioSongQuery.Comparable(RadioSongQuery.Clean(song.Artist, stationName));
        var head = candidates.Take(RankedCandidates)
            .Select((track, index) => (Track: track, Index: index, Score: Score(track, title, artist)))
            .OrderByDescending(c => c.Score)
            .ThenBy(c => c.Index)
            .Select(c => c.Track);
        return [.. head, .. candidates.Skip(RankedCandidates)];
    }

    // 2 when the title matches, plus 1 when an artist does (or, for titles without an artist, appears in the title).
    private static int Score(Track track, string title, string artist)
    {
        var score = 0;
        var trackTitle = RadioSongQuery.Comparable(RadioSongQuery.Clean(track.Title));
        if (trackTitle == title || Overlaps(trackTitle, title))
        {
            score += 2;
        }

        var announced = artist.Length > 0 ? artist : title;
        if (track.Artists.Any(credit => Overlaps(RadioSongQuery.Comparable(credit.Name), announced)))
        {
            score += 1;
        }

        return score;
    }

    // One contains the other ("titlefeatx" / "title", "artist1artist2" / "artist1"); very short names only match exactly.
    private static bool Overlaps(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0)
        {
            return false;
        }

        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 3 ? longer.Contains(shorter, StringComparison.Ordinal) : shorter == longer;
    }

    private void Remember(string query, IReadOnlyList<Track> tracks)
    {
        lock (_gate)
        {
            if (_cache.ContainsKey(query))
            {
                return;
            }

            _cache[query] = tracks;
            _cacheOrder.Enqueue(query);
            while (_cacheOrder.Count > CacheSize)
            {
                _cache.Remove(_cacheOrder.Dequeue());
            }
        }
    }
}
