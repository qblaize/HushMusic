using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// What an Explore "See all" opens (<see cref="ExploreCategoryRequest"/>): a mood or genre, every mood and genre, the
/// full new-release lists, or a country's charts.
/// </summary>
public sealed partial class ExploreCategoryViewModel(IExploreApi explore, PageServices services) : PageViewModelBase(services)
{
    private const string MoodCategoryBrowseId = "FEmusic_moods_and_genres_category";

    private ExploreCategoryRequest? _request;
    private CancellationTokenSource? _chartsCts;
    private bool _settingCountry;

    /// <summary>Carousels (a mood or genre page, the charts' playlist carousels).</summary>
    public ObservableCollection<SeeAllShelf> Sections { get; } = [];

    /// <summary>One list shown as a grid of cards (new releases, new videos, a category with a single shelf).</summary>
    public ObservableCollection<MediaItem> GridItems { get; } = [];

    public ObservableCollection<MoodCategoryGroup> MoodGroups { get; } = [];

    public ObservableCollection<RankedArtist> TopArtists { get; } = [];

    public ObservableCollection<CountryOption> Countries { get; } = [];

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Eyebrow { get; set; }

    [ObservableProperty]
    public partial bool ShowGrid { get; set; }

    /// <summary>The grid holds music videos (16:9 cards).</summary>
    [ObservableProperty]
    public partial bool IsVideoGrid { get; set; }

    [ObservableProperty]
    public partial bool ShowMoods { get; set; }

    [ObservableProperty]
    public partial bool ShowCharts { get; set; }

    [ObservableProperty]
    public partial bool HasTopArtists { get; set; }

    [ObservableProperty]
    public partial bool IsChartsBusy { get; set; }

    [ObservableProperty]
    public partial int SelectedCountryIndex { get; set; } = -1;

    public CountryOption? SelectedCountry => SelectedCountryIndex >= 0 && SelectedCountryIndex < Countries.Count ? Countries[SelectedCountryIndex] : null;

    /// <summary>Message of the empty state, by what the page lists.</summary>
    public string EmptyMessage => _request switch
    {
        ChartsRequest => "YouTube Music has no charts for this country right now.",
        NewVideosRequest => "YouTube Music didn't return any new videos.",
        NewReleasesRequest => "YouTube Music didn't return any new releases.",
        _ => "YouTube Music didn't return anything for this category.",
    };

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        _request = parameter as ExploreCategoryRequest;
        Title = _request?.Title ?? string.Empty;
        Eyebrow = _request switch
        {
            MoodRequest => "Moods & genres",
            ChartsRequest => null,
            _ => "Explore",
        };
        OnPropertyChanged(nameof(EmptyMessage));
        return LoadAsync();
    }

    protected override void OnNavigatedFromCore() => _chartsCts?.Cancel();

    protected override Task LoadAsync()
    {
        if (_request is not { } request)
        {
            ErrorMessage = "No category was selected.";
            return Task.CompletedTask;
        }

        return RunAsync(
            async ct =>
            {
                switch (request)
                {
                    case MoodRequest mood:
                        ShowMoodPage(await explore.GetMoodPlaylistsAsync(mood.Params, ct), mood);
                        break;
                    case MoodsAndGenresRequest:
                        Replace(MoodGroups, (await explore.GetMoodCategoriesAsync(ct)).Where(g => g.Categories.Count > 0));
                        ShowMoods = MoodGroups.Count > 0;
                        IsEmpty = !ShowMoods;
                        break;
                    case NewReleasesRequest:
                        ShowGridOf(await explore.GetNewReleasesAsync(ct), videos: false);
                        break;
                    case NewVideosRequest:
                        ShowGridOf(await explore.GetNewVideosAsync(ct), videos: true);
                        break;
                    case ChartsRequest charts:
                        ShowCharts = true;
                        ShowChartsPage(await explore.GetChartsAsync(SelectedCountry?.Code ?? charts.Country, ct), SelectedCountry?.Code ?? charts.Country);
                        break;
                }

                HasContent = true;
            },
            "Couldn't load this category");
    }

    [RelayCommand]
    private void OpenMood(MoodCategory? category)
    {
        if (category is not null)
        {
            Services.Navigation.NavigateTo(PageKey.ExploreCategory, new MoodRequest(category.Title, category.Params, category.Color));
        }
    }

    [RelayCommand]
    private void OpenArtist(RankedArtist? artist) => Actions.Open(artist?.Artist);

    partial void OnSelectedCountryIndexChanged(int value)
    {
        if (!_settingCountry && ShowCharts && HasContent && SelectedCountry is { } country)
        {
            _ = ReloadChartsAsync(country.Code);
        }
    }

    private void ShowMoodPage(MoodPage page, MoodRequest request)
    {
        if (page.Title.Length > 0)
        {
            Title = page.Title;
        }

        // A category with a single shelf (often a narrower one, opened from "See all") reads better as one grid.
        if (page.Sections is [{ Layout: ShelfLayout.Cards } only])
        {
            ShowGridOf(only.Items, videos: only.Items.All(i => i is Track { Type: TrackType.Video }));
            return;
        }

        Replace(Sections, page.Sections.Select(shelf => new SeeAllShelf(shelf, SeeAllCommandFor(shelf, request))));
        ShowGrid = false;
        IsEmpty = Sections.Count == 0;
    }

    // A shelf's title links to a narrower category of the same mood or genre.
    private RelayCommand? SeeAllCommandFor(Shelf shelf, MoodRequest parent) =>
        shelf.MoreBrowseId == MoodCategoryBrowseId && shelf.MoreParams is { Length: > 0 } moreParams
            ? new RelayCommand(() => Services.Navigation.NavigateTo(PageKey.ExploreCategory, new MoodRequest(shelf.Title, moreParams, parent.Color)))
            : null;

    private void ShowGridOf(IEnumerable<MediaItem> items, bool videos)
    {
        // The card template follows IsVideoGrid, so it changes before the cards are created.
        IsVideoGrid = videos;
        Replace(GridItems, items);
        ShowGrid = true;
        IsEmpty = GridItems.Count == 0;
    }

    private void ShowChartsPage(ChartsPage charts, string requestedCountry)
    {
        _settingCountry = true;
        try
        {
            var countries = CountryOption.Sorted(charts.Countries);
            if (countries.Count > 0 && !countries.SequenceEqual(Countries))
            {
                Replace(Countries, countries);
            }

            var selected = Countries.FirstOrDefault(c => string.Equals(c.Code, requestedCountry, StringComparison.OrdinalIgnoreCase))
                ?? Countries.FirstOrDefault(c => string.Equals(c.Name, charts.CountryName, StringComparison.OrdinalIgnoreCase));
            SelectedCountryIndex = selected is null ? -1 : Countries.IndexOf(selected);
        }
        finally
        {
            _settingCountry = false;
        }

        Replace(TopArtists, charts.TopArtists.Select(entry => new RankedArtist(entry)));
        HasTopArtists = TopArtists.Count > 0;
        Replace(Sections, charts.PlaylistCharts.Where(s => s.Items.Count > 0).Select(s => new SeeAllShelf(s)));
        IsEmpty = !HasTopArtists && Sections.Count == 0;
    }

    private async Task ReloadChartsAsync(string country)
    {
        _chartsCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(NavigationToken);
        _chartsCts = cts;
        IsChartsBusy = true;
        try
        {
            ShowChartsPage(await explore.GetChartsAsync(country, cts.Token), country);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportError("Couldn't load the charts", ex);
        }
        finally
        {
            if (ReferenceEquals(_chartsCts, cts))
            {
                IsChartsBusy = false;
                _chartsCts = null;
            }
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
