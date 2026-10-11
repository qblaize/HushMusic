using System.Text;
using HushMusic.Core.Features.Stats;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>A station the user listened to, with the total time (from the local play log).</summary>
public sealed record ListenedStation(string StationId, string Name, TimeSpan Listened);

/// <summary>A genre tag and how much the user likes it.</summary>
public sealed record GenreWeight(string Tag, double Weight);

/// <summary>The genre a day's station comes from.</summary>
/// <param name="Tag">The Radio Browser tag to look up ("deep house").</param>
/// <param name="Label">For display: the curated genre's name when it is one, else the tag capitalized.</param>
public sealed record GenreChoice(string Tag, string Label, bool IsFromTaste);

/// <summary>
/// The "Station of the day" choices, without I/O: which genres the user likes (from their favourite stations and the
/// stations they listen to most), the genre for a day, and a station from it. Every choice is seeded by the date, so the
/// same inputs give the same station all day.
/// </summary>
public static class StationOfTheDayPicker
{
    /// <summary>How many of the best-liked genres take turns.</summary>
    public const int TasteSize = 5;

    /// <summary>Only the most played stations of a genre are picked from (the directory lists them by plays).</summary>
    public const int CandidatePool = 30;

    /// <summary>Less listening than this (all stations together) says nothing about taste.</summary>
    public static readonly TimeSpan MinimumListening = TimeSpan.FromMinutes(5);

    private const double FavoriteWeight = 3;
    private const double MaxListeningWeight = 10;
    private const int TagsPerStation = 3;

    // Directory tags that describe the station, the format or the language rather than a kind of music.
    private static readonly HashSet<string> NotGenres = new(StringComparer.Ordinal)
    {
        "music", "radio", "fm", "am", "online", "internet", "internet radio", "webradio", "web radio", "live", "stereo",
        "free", "non-stop", "nonstop", "local", "community", "various", "variety", "news", "talk", "hits", "public radio",
        "commercial", "entertainment", "mix", "24/7",
    };

    /// <summary>Total listening per radio station in <paramref name="plays"/>.</summary>
    public static IReadOnlyList<ListenedStation> Listening(IEnumerable<PlayRecord> plays)
    {
        ArgumentNullException.ThrowIfNull(plays);
        return [.. plays
            .Where(p => p.Kind == PlayKind.Radio && !string.IsNullOrEmpty(p.ItemId))
            .GroupBy(p => p.ItemId, StringComparer.Ordinal)
            .Select(g => new ListenedStation(g.Key, g.Last().Title, TimeSpan.FromSeconds(g.Sum(p => (double)p.ListenedSeconds))))
            .OrderByDescending(s => s.Listened)];
    }

    /// <summary>
    /// The user's genres, best liked first (at most <see cref="TasteSize"/>). A favourite station counts as much as three
    /// hours of listening; listening counts up to ten hours per station. A station's first tag weighs double. Stations that
    /// are neither favourites nor curated have no known tags, so genre names in their name stand in ("Deep House Radio").
    /// </summary>
    public static IReadOnlyList<GenreWeight> Taste(IReadOnlyList<RadioStation> favorites, IReadOnlyList<ListenedStation> listened, IReadOnlyList<RadioGenre> genres)
    {
        ArgumentNullException.ThrowIfNull(favorites);
        ArgumentNullException.ThrowIfNull(listened);
        ArgumentNullException.ThrowIfNull(genres);

        var known = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var genre in genres)
        {
            foreach (var station in genre.Stations)
            {
                known.TryAdd(station.Id, [.. genre.DirectoryTags, .. station.Tags]);
            }
        }

        foreach (var station in favorites)
        {
            known[station.Id] = station.Tags;
        }

        var vocabulary = known.Values.SelectMany(t => t).Select(NormalizeTag).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var station in favorites)
        {
            Add(station.Tags, FavoriteWeight);
        }

        if (listened.Sum(l => l.Listened.TotalSeconds) >= MinimumListening.TotalSeconds)
        {
            foreach (var station in listened.Where(l => l.Listened > TimeSpan.Zero))
            {
                var tags = known.TryGetValue(station.StationId, out var stationTags) ? stationTags : TagsInName(station.Name, vocabulary);
                Add(tags, Math.Min(station.Listened.TotalHours, MaxListeningWeight));
            }
        }

        return [.. weights
            .OrderByDescending(w => w.Value)
            .ThenBy(w => w.Key, StringComparer.Ordinal)
            .Take(TasteSize)
            .Select(w => new GenreWeight(w.Key, w.Value))];

        void Add(IReadOnlyList<string> tags, double weight)
        {
            var index = 0;
            foreach (var tag in tags.Select(NormalizeTag).OfType<string>().Distinct(StringComparer.Ordinal).Take(TagsPerStation))
            {
                weights[tag] = weights.GetValueOrDefault(tag) + (index++ == 0 ? weight : weight / 2);
            }
        }
    }

    /// <summary>
    /// The genre for <paramref name="day"/>: one of the user's genres, more likely the better liked it is; without any, one
    /// of the curated genres in turn.
    /// </summary>
    public static GenreChoice? ChooseGenre(IReadOnlyList<GenreWeight> taste, IReadOnlyList<RadioGenre> genres, DateOnly day)
    {
        ArgumentNullException.ThrowIfNull(taste);
        ArgumentNullException.ThrowIfNull(genres);
        var total = taste.Sum(t => t.Weight);
        if (total > 0)
        {
            var target = Unit(day, 0) * total;
            var chosen = taste[^1];
            foreach (var genre in taste)
            {
                target -= genre.Weight;
                if (target < 0)
                {
                    chosen = genre;
                    break;
                }
            }

            return new GenreChoice(chosen.Tag, Label(chosen.Tag, genres), IsFromTaste: true);
        }

        var curated = genres.Where(g => g.DirectoryTags.Count > 0).ToList();
        if (curated.Count == 0)
        {
            return null;
        }

        var fallback = curated[day.DayNumber % curated.Count];
        return new GenreChoice(NormalizeTag(fallback.DirectoryTags[0]) ?? fallback.DirectoryTags[0], fallback.Name, IsFromTaste: false);
    }

    /// <summary>
    /// The stations to pick from: curated ones of the genre first, then the directory's (most played first), at most
    /// <see cref="CandidatePool"/>, without the user's favourites and without repeats.
    /// </summary>
    public static IReadOnlyList<RadioStation> Candidates(string tag, IReadOnlyList<RadioStation> directory, IReadOnlyList<RadioGenre> genres, IReadOnlySet<string> favorites)
    {
        ArgumentNullException.ThrowIfNull(tag);
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(genres);
        ArgumentNullException.ThrowIfNull(favorites);

        var curated = genres
            .SelectMany(g => g.Stations.Where(s => HasTag(g.DirectoryTags, tag) || HasTag(s.Tags, tag)));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.Ordinal);
        return [.. curated.Concat(directory)
            .Where(s => !favorites.Contains(s.Id) && ids.Add(s.Id) && names.Add(Words(s.Name)))
            .Take(CandidatePool)];
    }

    /// <summary>
    /// A station from <paramref name="candidates"/> for the day: preferably one the user hasn't listened to yet, never
    /// <paramref name="skip"/>. <paramref name="attempt"/> 0 is the day's pick, 1 "Another one". Null when none is left.
    /// </summary>
    public static RadioStation? Pick(IReadOnlyList<RadioStation> candidates, DateOnly day, int attempt, IReadOnlySet<string> heard, string? skip = null)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(heard);
        var pool = candidates.Where(s => s.Id != skip).ToList();
        var fresh = pool.Where(s => !heard.Contains(s.Id)).ToList();
        if (fresh.Count > 0)
        {
            pool = fresh;
        }

        return pool.Count == 0 ? null : pool[Math.Min((int)(Unit(day, 1 + attempt) * pool.Count), pool.Count - 1)];
    }

    /// <summary>"Deep  House" → "deep house"; null for tags that aren't a kind of music ("radio", "#charts", "2000s").</summary>
    public static string? NormalizeTag(string? tag)
    {
        var value = string.Join(' ', (tag ?? string.Empty).Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (value.Length is < 3 or > 30 || value.StartsWith('#') || !value.Any(char.IsLetter) || value.Any(char.IsDigit) || NotGenres.Contains(value))
        {
            return null;
        }

        return value;
    }

    // A number in [0, 1) that depends only on the date and the salt (SplitMix64), so a choice can't change during the day.
    // Not System.Random: its first value for nearby seeds follows a pattern, and string hashes change every run.
    private static double Unit(DateOnly day, int salt)
    {
        unchecked
        {
            var x = ((ulong)day.DayNumber * 0x9E3779B97F4A7C15UL) + ((ulong)salt * 0xD1B54A32D192ED03UL);
            x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
            x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
            x ^= x >> 31;
            return (x >> 11) * (1.0 / (1UL << 53));
        }
    }

    private static bool HasTag(IReadOnlyList<string> tags, string tag) => tags.Any(t => NormalizeTag(t) == tag);

    private static string Label(string tag, IReadOnlyList<RadioGenre> genres) =>
        genres.FirstOrDefault(g => HasTag(g.DirectoryTags, tag))?.Name ?? char.ToUpperInvariant(tag[0]) + tag[1..];

    // Known genres named as whole words in a station's name: "1.FM - Deep Techno & Deep House" → deep house, techno.
    private static List<string> TagsInName(string name, HashSet<string> vocabulary)
    {
        var words = " " + Words(name) + " ";
        return [.. vocabulary.Where(tag => words.Contains(" " + tag + " ", StringComparison.Ordinal)).OrderByDescending(t => t.Length).ThenBy(t => t, StringComparer.Ordinal)];
    }

    // Lower-case words separated by single spaces, punctuation dropped.
    private static string Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
