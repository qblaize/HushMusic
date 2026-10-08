using System.Globalization;
using System.Windows.Input;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>What an Explore "See all" opens on the <see cref="PageKey.ExploreCategory"/> page.</summary>
/// <remarks>Records, so that opening the same category twice in a row is recognised as the same page.</remarks>
public abstract record ExploreCategoryRequest(string Title);

/// <summary>A mood or genre, or a narrower category inside one.</summary>
public sealed record MoodRequest(string Title, string Params, uint? Color = null) : ExploreCategoryRequest(Title);

/// <summary>Every mood and genre, grouped.</summary>
public sealed record MoodsAndGenresRequest() : ExploreCategoryRequest("Moods & genres");

/// <summary>Every new album and single.</summary>
public sealed record NewReleasesRequest() : ExploreCategoryRequest("New releases");

/// <summary>Every new music video.</summary>
public sealed record NewVideosRequest() : ExploreCategoryRequest("New music videos");

/// <summary>The full charts of one country ("ZZ" is Global).</summary>
public sealed record ChartsRequest(string Country) : ExploreCategoryRequest("Charts");

/// <summary>
/// A shelf with an optional "See all". Cards and compact song rows ("Quick picks" style) are offered separately so a
/// template can give each layout its own carousel.
/// </summary>
public sealed class SeeAllShelf(Shelf shelf, ICommand? seeAllCommand = null)
{
    public Shelf Shelf { get; } = shelf;

    public string Title => Shelf.Title;

    public string? Subtitle => Shelf.Subtitle;

    public IReadOnlyList<MediaItem> Items => Shelf.Items;

    /// <summary>The items when the shelf is a carousel of cards; null otherwise.</summary>
    public IReadOnlyList<MediaItem>? CardItems => Shelf.Layout == ShelfLayout.Cards ? Shelf.Items : null;

    /// <summary>The items when the shelf is a grid of song rows; null otherwise.</summary>
    public IReadOnlyList<MediaItem>? RowItems => Shelf.Layout == ShelfLayout.List ? Shelf.Items : null;

    public bool ShowsRows => Shelf.Layout == ShelfLayout.List;

    public ICommand? SeeAllCommand { get; } = seeAllCommand;

    /// <summary>Label of the header's action; null hides it.</summary>
    public string? SeeAllText => SeeAllCommand is null ? null : "See all";
}

/// <summary>An artist on a chart, with its position and movement when YouTube Music sends them.</summary>
public sealed class RankedArtist(ChartEntry<Artist> entry)
{
    public Artist Artist { get; } = entry.Item;

    public string RankText { get; } = entry.Rank?.ToString(CultureInfo.CurrentCulture) ?? string.Empty;

    public ChartTrend? Trend { get; } = entry.Trend;

    /// <summary>Screen-reader text for the trend arrow.</summary>
    public string TrendName { get; } = entry.Trend switch
    {
        ChartTrend.Up => "Up",
        ChartTrend.Down => "Down",
        ChartTrend.Neutral => "No change",
        _ => string.Empty,
    };
}

/// <summary>A chart country for the picker.</summary>
public sealed record CountryOption(string Code, string Name)
{
    public const string GlobalCode = "ZZ";

    /// <summary>Names come from .NET's region data, in English like the rest of the app; "ZZ" is YouTube's Global chart.</summary>
    public static CountryOption For(string code)
    {
        if (string.Equals(code, GlobalCode, StringComparison.OrdinalIgnoreCase))
        {
            return new CountryOption(GlobalCode, "Global");
        }

        try
        {
            return new CountryOption(code, new RegionInfo(code).EnglishName);
        }
        catch (ArgumentException)
        {
            return new CountryOption(code, code);
        }
    }

    /// <summary>Global first, then by name.</summary>
    public static List<CountryOption> Sorted(IEnumerable<string> codes) =>
        [.. codes.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(For)
            .OrderBy(c => c.Code == GlobalCode ? 0 : 1)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)];

    public override string ToString() => Name;
}
