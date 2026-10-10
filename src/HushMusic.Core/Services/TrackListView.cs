using System.Globalization;
using System.Text;
using HushMusic.Core.Models;

namespace HushMusic.Core.Services;

/// <summary>What a track list is sorted by. <see cref="Custom"/> is the list's own order (a playlist's order).</summary>
public enum TrackSort
{
    Custom,
    Title,
    Artist,
    Album,
    Duration,
}

/// <summary>How a track list is shown: which tracks (a filter) in which order.</summary>
/// <param name="Filter">Words that must all appear in the title, the artists or the album, in any case, with or without accents.</param>
/// <param name="Descending">Reverses the order; with <see cref="TrackSort.Custom"/> the list is shown last to first.</param>
public sealed record TrackListOptions(string Filter = "", TrackSort Sort = TrackSort.Custom, bool Descending = false)
{
    public static TrackListOptions Default { get; } = new();

    public bool IsFiltered => TrackListView.Terms(Filter).Count > 0;

    /// <summary>The order differs from the list's own.</summary>
    public bool IsSorted => Sort != TrackSort.Custom || Descending;

    /// <summary>Every track, in the list's own order.</summary>
    public bool IsDefault => !IsFiltered && !IsSorted;
}

/// <summary>Text folding for <see cref="TrackListView{T}"/>: what a filter matches against.</summary>
public static class TrackListView
{
    /// <summary>
    /// Folds text for matching: lower case, accents and other marks removed ("Beyoncé" → "beyonce", "Ștefan" → "stefan"),
    /// compatibility forms unified (full-width letters, ligatures), a few letters without a decomposition spelled out
    /// ("ß" → "ss", "ø" → "o") and typographic apostrophes and dashes made plain.
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var decomposed = text.Normalize(NormalizationForm.FormKD);
        var folded = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            switch (char.ToLowerInvariant(c))
            {
                case 'ß':
                    folded.Append("ss");
                    break;
                case 'æ':
                    folded.Append("ae");
                    break;
                case 'œ':
                    folded.Append("oe");
                    break;
                case 'þ':
                    folded.Append("th");
                    break;
                case 'ø':
                    folded.Append('o');
                    break;
                case 'đ' or 'ð':
                    folded.Append('d');
                    break;
                case 'ł':
                    folded.Append('l');
                    break;
                case 'ı':
                    folded.Append('i');
                    break;
                case '‘' or '’' or 'ʼ' or '`':
                    folded.Append('\'');
                    break;
                case '“' or '”' or '„':
                    folded.Append('"');
                    break;
                case '‐' or '‑' or '‒' or '–' or '—':
                    folded.Append('-');
                    break;
                case var lower:
                    folded.Append(lower);
                    break;
            }
        }

        return folded.ToString();
    }

    /// <summary>The folded words of a filter (none for an empty or blank one).</summary>
    public static IReadOnlyList<string> Terms(string? filter) =>
        Normalize(filter).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>The folded title, artists and album of <paramref name="track"/>, one per line.</summary>
    public static string SearchText(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Normalize($"{track.Title}\n{track.ArtistsText}\n{track.Album?.Name}");
    }

    /// <summary>True when every term occurs in <paramref name="searchText"/> (from <see cref="SearchText"/>).</summary>
    public static bool Matches(string searchText, IReadOnlyList<string> terms)
    {
        ArgumentNullException.ThrowIfNull(searchText);
        ArgumentNullException.ThrowIfNull(terms);
        foreach (var term in terms)
        {
            if (!searchText.Contains(term, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Filters and sorts a track list for display (<see cref="Apply"/>). The sort is stable: tracks that compare equal keep
/// the list's order, in both directions, and tracks without the value sorted by (no album, no duration) always come last.
/// Text sorts follow the culture's alphabet and ignore case and accents.
/// </summary>
/// <remarks>
/// The folded search text of each item is kept between calls, so filtering a long list again on every keystroke is a
/// plain scan. Items are told apart by reference (the same song listed twice is two items).
/// </remarks>
/// <typeparam name="T">A row of the list.</typeparam>
public sealed class TrackListView<T>
    where T : class
{
    private const CompareOptions TextOptions =
        CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth;

    private readonly Func<T, Track> _track;
    private readonly CompareInfo _compare;
    private Dictionary<T, string> _searchText = new(ReferenceEqualityComparer.Instance);

    /// <param name="track">The track a row shows.</param>
    /// <param name="culture">The alphabet for text sorts; the current culture when null.</param>
    public TrackListView(Func<T, Track> track, CultureInfo? culture = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
        _compare = (culture ?? CultureInfo.CurrentCulture).CompareInfo;
    }

    /// <summary>The rows of <paramref name="items"/> (in the list's order) that match the filter, in the chosen order.</summary>
    public List<T> Apply(IReadOnlyList<T> items, TrackListOptions options)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(options);

        var terms = TrackListView.Terms(options.Filter);
        var rows = new List<Row>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (terms.Count == 0 || TrackListView.Matches(SearchTextOf(item), terms))
            {
                rows.Add(new Row(item, _track(item), i));
            }
        }

        if (options.IsSorted)
        {
            var sort = options.Sort;
            var descending = options.Descending;
            rows.Sort((x, y) => CompareRows(x, y, sort, descending));
        }

        ForgetRemovedItems(items);
        return rows.ConvertAll(row => row.Item);
    }

    private int CompareRows(Row x, Row y, TrackSort sort, bool descending)
    {
        var result = sort switch
        {
            TrackSort.Title => CompareText(x.Track.Title, y.Track.Title, descending),
            TrackSort.Artist => CompareText(x.Track.ArtistsText, y.Track.ArtistsText, descending),
            TrackSort.Album => CompareText(x.Track.Album?.Name, y.Track.Album?.Name, descending),
            TrackSort.Duration => CompareDuration(x.Track.Duration, y.Track.Duration, descending),
            _ => 0,
        };

        if (result != 0)
        {
            return result;
        }

        // Ties keep the list's order; the list's own order reversed is the one exception.
        return sort == TrackSort.Custom && descending ? y.Index.CompareTo(x.Index) : x.Index.CompareTo(y.Index);
    }

    private int CompareText(string? x, string? y, bool descending)
    {
        var xMissing = string.IsNullOrWhiteSpace(x);
        var yMissing = string.IsNullOrWhiteSpace(y);
        if (xMissing || yMissing)
        {
            return MissingLast(xMissing, yMissing);
        }

        var result = _compare.Compare(x, y, TextOptions);
        return descending ? -result : result;
    }

    private static int CompareDuration(TimeSpan? x, TimeSpan? y, bool descending)
    {
        if (x is not { } first || y is not { } second)
        {
            return MissingLast(x is null, y is null);
        }

        var result = first.CompareTo(second);
        return descending ? -result : result;
    }

    private static int MissingLast(bool xMissing, bool yMissing) => xMissing == yMissing ? 0 : xMissing ? 1 : -1;

    private string SearchTextOf(T item)
    {
        if (!_searchText.TryGetValue(item, out var text))
        {
            text = TrackListView.SearchText(_track(item));
            _searchText[item] = text;
        }

        return text;
    }

    // Rows that left the list (removed songs, a reload) needn't be remembered.
    private void ForgetRemovedItems(IReadOnlyList<T> items)
    {
        if (_searchText.Count <= (2 * items.Count) + 64)
        {
            return;
        }

        var kept = new Dictionary<T, string>(items.Count, ReferenceEqualityComparer.Instance);
        foreach (var item in items)
        {
            if (_searchText.TryGetValue(item, out var text))
            {
                kept[item] = text;
            }
        }

        _searchText = kept;
    }

    private readonly record struct Row(T Item, Track Track, int Index);
}
