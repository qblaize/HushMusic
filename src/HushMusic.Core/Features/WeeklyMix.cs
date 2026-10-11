using System.Text.RegularExpressions;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;
using HushMusic.Core.Queue;

namespace HushMusic.Core.Features;

/// <summary>One week's "Fresh for you" mix.</summary>
public sealed record WeeklyMix(DateTimeOffset BuiltAt, IReadOnlyList<Track> Tracks);

/// <summary>
/// "Fresh for you": a weekly mix built on this PC from the local play log, with artists the user loves and songs they
/// haven't heard yet. Built in the background once a week (the first run on or after Monday), only with enough listening
/// history; it works signed out too. <see cref="Changed"/> is raised on a background thread. Implemented in Core.
/// </summary>
public interface IWeeklyMix
{
    /// <summary>The latest mix, or null (none built yet, or not enough listening history).</summary>
    WeeklyMix? Current { get; }

    event EventHandler? Changed;

    /// <summary>Completes once the saved mix is loaded (the first call starts loading it in the background).</summary>
    Task LoadAsync(CancellationToken cancellationToken = default);
}

/// <summary>What a mix is built from: the user's favourite songs (radio seeds) and artists (their top songs).</summary>
/// <param name="Plays">Songs played in the history window.</param>
public sealed record WeeklyMixSeeds(IReadOnlyList<string> Songs, IReadOnlyList<PlayArtist> Artists, int Plays);

/// <summary>
/// The songs in the play log, by id and by title and main artist: the same song is often on YouTube twice (the album
/// track and the music video), so a song heard as one isn't new as the other.
/// </summary>
public sealed class HeardSongs
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly HashSet<string> _titles = new(StringComparer.Ordinal);

    public static HeardSongs From(IEnumerable<PlayRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        var heard = new HeardSongs();
        foreach (var record in records.Where(r => r.Kind == PlayKind.Track))
        {
            heard._ids.Add(record.ItemId);
            heard._titles.Add(WeeklyMixPlanner.SongKey(record.Title, record.Artists.Count > 0 ? record.Artists[0].Name : null));
        }

        return heard;
    }

    public bool Contains(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return _ids.Contains(track.VideoId) || _titles.Contains(WeeklyMixPlanner.SongKey(track.Title, track.Artists.FirstOrDefault()?.Name));
    }
}

/// <summary>The rules of the weekly mix: when it is due, what it is seeded with and how candidates are merged. Pure.</summary>
public static partial class WeeklyMixPlanner
{
    /// <summary>Less listening than this in <see cref="HistoryWindow"/> and no mix is built.</summary>
    public const int MinimumPlays = 20;

    public const int TargetSize = 50;

    /// <summary>Songs by one artist in a mix, at most.</summary>
    public const int MaxPerArtist = 3;

    public const int SeedSongCount = 6;

    public const int SeedArtistCount = 6;

    /// <summary>The seeds come from the plays of the last eight weeks.</summary>
    public static readonly TimeSpan HistoryWindow = TimeSpan.FromDays(56);

    /// <summary>Monday 00:00 of the week <paramref name="now"/> is in, in <paramref name="zone"/>.</summary>
    public static DateTimeOffset StartOfWeek(DateTimeOffset now, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var monday = local.Date.AddDays(-(((int)local.DayOfWeek + 6) % 7));
        return new DateTimeOffset(monday, zone.GetUtcOffset(monday));
    }

    /// <summary>
    /// A new mix is due on the first run on or after Monday: when the last one was built before this week started (or
    /// never). One "built" in the future means the clock was turned back, and is replaced too.
    /// </summary>
    public static bool IsDue(DateTimeOffset? lastBuilt, DateTimeOffset now, TimeZoneInfo zone) =>
        lastBuilt is not { } built || built < StartOfWeek(now, zone) || built > now.AddDays(1);

    /// <summary>
    /// The seeds, from the songs played (long enough to count) in the <see cref="HistoryWindow"/> before
    /// <paramref name="now"/>: the most played songs, each by a different main artist where possible, and the most played
    /// artists that have a page. Null when there were fewer than <see cref="MinimumPlays"/> plays.
    /// </summary>
    public static WeeklyMixSeeds? PickSeeds(IEnumerable<PlayRecord> records, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(records);
        var from = now - HistoryWindow;
        var plays = records.Where(r => r.Kind == PlayKind.Track && r.IsPlay && r.Start >= from && r.Start <= now).ToList();
        if (plays.Count < MinimumPlays)
        {
            return null;
        }

        // Most played first; ties go to the most recently played.
        var songs = plays
            .GroupBy(r => r.ItemId, StringComparer.Ordinal)
            .Select(g => (VideoId: g.Key, Artist: MainArtist(g.MaxBy(r => r.Start)!), Plays: g.Count(), Last: g.Max(r => r.Start)))
            .OrderByDescending(s => s.Plays)
            .ThenByDescending(s => s.Last)
            .ToList();

        var artistsUsed = new HashSet<string>(StringComparer.Ordinal);
        List<string> seedSongs = [.. songs.Where(s => s.Artist is null || artistsUsed.Add(s.Artist)).Take(SeedSongCount).Select(s => s.VideoId)];
        List<string> more = [.. songs.Select(s => s.VideoId).Except(seedSongs, StringComparer.Ordinal).Take(SeedSongCount - seedSongs.Count)];
        seedSongs.AddRange(more);

        List<PlayArtist> seedArtists =
        [
            .. plays
                .SelectMany(r => r.Artists.Where(a => !string.IsNullOrWhiteSpace(a.BrowseId)).DistinctBy(a => a.BrowseId).Select(a => (Artist: a, r.Start)))
                .GroupBy(p => p.Artist.BrowseId!, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .ThenByDescending(g => g.Max(p => p.Start))
                .Take(SeedArtistCount)
                .Select(g => g.MaxBy(p => p.Start).Artist),
        ];

        return new WeeklyMixSeeds(seedSongs, seedArtists, plays.Count);
    }

    /// <summary>
    /// Takes songs from each source in turn (the order within a source is its own ranking), leaving out songs already
    /// heard, songs <paramref name="leaveOut"/> rejects, duplicates (also the same title by the same artist under another
    /// id), live streams, episodes and more than <see cref="MaxPerArtist"/> songs per artist, then spreads each artist's
    /// songs over the mix (<see cref="ShuffleOrder"/>).
    /// </summary>
    public static List<Track> Merge(
        IReadOnlyList<IReadOnlyList<Track>> sources,
        HeardSongs heard,
        Func<string, bool> leaveOut,
        Random random,
        int size = TargetSize)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(heard);
        ArgumentNullException.ThrowIfNull(leaveOut);
        ArgumentNullException.ThrowIfNull(random);

        var picked = new List<Track>(size);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var titles = new HashSet<string>(StringComparer.Ordinal);
        var perArtist = new Dictionary<string, int>(StringComparer.Ordinal);
        var next = new int[sources.Count];
        var progress = true;
        while (picked.Count < size && progress)
        {
            progress = false;
            for (var s = 0; s < sources.Count && picked.Count < size; s++)
            {
                var source = sources[s];
                while (next[s] < source.Count)
                {
                    var track = source[next[s]++];
                    if (Accept(track))
                    {
                        picked.Add(track);
                        progress = true;
                        break;
                    }
                }
            }
        }

        return ShuffleOrder.Spread(picked, t => t.Artists.FirstOrDefault()?.Name, random);

        bool Accept(Track track)
        {
            if (!track.IsAvailable || track.IsLiveRadio || track.Type == TrackType.Episode || string.IsNullOrWhiteSpace(track.VideoId)
                || ids.Contains(track.VideoId) || leaveOut(track.VideoId) || heard.Contains(track))
            {
                return false;
            }

            var artist = Normalize(track.Artists.FirstOrDefault()?.Name);
            if (!titles.Add(SongKey(track.Title, artist)))
            {
                return false;
            }

            if (artist.Length > 0)
            {
                var count = perArtist.GetValueOrDefault(artist);
                if (count >= MaxPerArtist)
                {
                    return false;
                }

                perArtist[artist] = count + 1;
            }

            ids.Add(track.VideoId);
            return true;
        }
    }

    /// <summary>
    /// Tells songs apart by title and main artist, ignoring case and the versions YouTube adds to a title: "Song (2022
    /// Remaster)", "Song [Official Video]" and "Artist - Song" are all the same song.
    /// </summary>
    internal static string SongKey(string? title, string? artist)
    {
        var name = Normalize(artist);
        var song = Normalize(title);
        var bare = Bracketed().Replace(song, string.Empty).Trim();
        if (name.Length > 0 && bare.StartsWith(name + " - ", StringComparison.Ordinal))
        {
            bare = bare[(name.Length + 3)..].Trim();
        }

        return (bare.Length > 0 ? bare : song) + "\n" + name;
    }

    private static string? MainArtist(PlayRecord record) =>
        record.Artists.Count > 0 ? record.Artists[0].BrowseId ?? Normalize(record.Artists[0].Name) : null;

    private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToUpperInvariant();

    [GeneratedRegex(@"\s*[(\[][^)\]]*[)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex Bracketed();
}
