using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class SearchViewModel : PageViewModelBase
{
    private readonly ISearchApi _search;
    private readonly IRecentSearches _recent;

    public SearchViewModel(ISearchApi search, IRecentSearches recent, PageServices services)
        : base(services)
    {
        _search = search;
        _recent = recent;
        Results = new IncrementalCollection<MediaItem>(
            FetchMoreAsync,
            ex => ReportError("Couldn't load more results", ex),
            () => NavigationToken);
    }

    /// <summary>Titled sections of an unfiltered search.</summary>
    public ObservableCollection<MediaGroup> Groups { get; } = [];

    /// <summary>Results shown as one list (filtered search, or an unfiltered one without sections), with a continuation.</summary>
    public IncrementalCollection<MediaItem> Results { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    [NotifyPropertyChangedFor(nameof(ShowPrompt))]
    public partial string? Query { get; set; }

    [ObservableProperty]
    public partial SearchFilter Filter { get; set; }

    /// <summary>True when the results come in titled sections (shown as groups); otherwise one flat list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowGroupedResults))]
    [NotifyPropertyChangedFor(nameof(ShowFlatResults))]
    public partial bool IsGrouped { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TopResultTrack))]
    [NotifyPropertyChangedFor(nameof(HasTopResult))]
    [NotifyPropertyChangedFor(nameof(CanPlayTopResult))]
    public partial MediaItem? TopResult { get; set; }

    public string Heading => Query is null ? "Search" : $"Results for “{Query}”";

    public bool ShowPrompt => Query is null;

    public bool ShowGroupedResults => IsGrouped;

    public bool ShowFlatResults => !IsGrouped;

    public bool HasTopResult => TopResult is not null;

    public bool CanPlayTopResult => TopResult is Track or Album or Playlist;

    /// <summary>Gives the top result a track context menu when it is a song.</summary>
    public Track? TopResultTrack => TopResult as Track;

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        if (parameter is string query && !string.IsNullOrWhiteSpace(query) && query.Trim() != Query)
        {
            Query = query.Trim();
            Filter = SearchFilter.All;
            return LoadAsync();
        }

        // Same query (back navigation to the cached page): keep the results unless the last load never finished.
        return Query is not null && !HasContent ? LoadAsync() : Task.CompletedTask;
    }

    protected override Task LoadAsync()
    {
        if (Query is not { } query)
        {
            return Task.CompletedTask;
        }

        var filter = Filter;
        ClearResults();
        return RunAsync(
            async ct =>
            {
                var results = await _search.SearchAsync(query, filter, null, ct);
                var sections = results.Sections.Where(s => s.Items.Count > 0).ToList();
                TopResult = filter == SearchFilter.All ? results.TopResult : null;

                // Older layouts return one titled section per category; the current unfiltered layout
                // returns a single untitled shelf, which is shown as a plain list without a heading.
                IsGrouped = filter == SearchFilter.All && sections.Any(s => !string.IsNullOrWhiteSpace(s.Title));
                if (IsGrouped)
                {
                    foreach (var section in sections)
                    {
                        var title = string.IsNullOrWhiteSpace(section.Title) ? "More results" : section.Title;
                        Groups.Add(new MediaGroup(title, section.Items) { ShowAllCommand = CreateShowAllCommand(title) });
                    }
                }
                else
                {
                    Results.Reset(sections.SelectMany(s => s.Items), results.Continuation);
                }

                IsEmpty = TopResult is null && Groups.Count == 0 && Results.Count == 0;
                Services.Warmup.Warm(results.TopResult as Track ?? results.AllItems.OfType<Track>().FirstOrDefault(t => t.IsAvailable));
                HasContent = true;
            },
            "Search failed");
    }

    [RelayCommand]
    private Task SelectFilterAsync(SearchFilter filter)
    {
        if (filter == Filter)
        {
            return Task.CompletedTask;
        }

        Filter = filter;
        return LoadAsync();
    }

    [RelayCommand]
    private Task LoadMoreAsync() => Results.LoadMoreAsync();

    [RelayCommand]
    private Task PlayTopResultAsync()
    {
        _recent.Add(Query);
        return Actions.PlayAsync(TopResult);
    }

    /// <summary>A result was chosen: the query goes (back) to the top of the recent searches.</summary>
    [RelayCommand]
    private void OpenResult(object? item)
    {
        _recent.Add(Query);
        Actions.Open(item);
    }

    private async Task<Paged<MediaItem>> FetchMoreAsync(string continuation, CancellationToken cancellationToken)
    {
        var results = await _search.SearchAsync(Query ?? string.Empty, Filter, continuation, cancellationToken);
        return new Paged<MediaItem>([.. results.AllItems], results.Continuation);
    }

    private void ClearResults()
    {
        HasContent = false;
        IsEmpty = false;
        IsGrouped = false;
        TopResult = null;
        Groups.Clear();
        Results.Reset([], null);
    }

    // Section titles come from YouTube Music (English UI); unknown titles simply get no "Show all".
    private RelayCommand? CreateShowAllCommand(string sectionTitle)
    {
        SearchFilter? filter = sectionTitle.Trim().ToLowerInvariant() switch
        {
            "songs" => SearchFilter.Songs,
            "videos" => SearchFilter.Videos,
            "albums" => SearchFilter.Albums,
            "artists" => SearchFilter.Artists,
            "community playlists" => SearchFilter.CommunityPlaylists,
            "featured playlists" => SearchFilter.FeaturedPlaylists,
            _ => null,
        };
        return filter is { } value ? new RelayCommand(() => SelectFilterCommand.Execute(value)) : null;
    }
}
