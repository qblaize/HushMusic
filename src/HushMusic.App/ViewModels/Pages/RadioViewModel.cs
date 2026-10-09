using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;
using HushMusic.Core.Radio;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// The Radio page: genre chips over the curated stations of the selected genre and more of the genre from the Radio
/// Browser directory, the user's favourites, and a directory search. Playing a station queues the section it is in, so
/// Next and Previous switch stations. The page is cached only while it is the most recent tab (see NavigationService),
/// so the genre and the search picked last outlive it, and it follows the player and the favourites only while shown.
/// </summary>
public sealed partial class RadioViewModel : PageViewModelBase, IStationHost
{
    private const int DirectoryLimit = 36;
    private const int SearchLimit = 60;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    private static string? s_genreId;
    private static string s_query = string.Empty;

    private readonly IRadioDirectory _directory;
    private readonly IRadioFavorites _favorites;
    private readonly IPlayer _player;
    private readonly ILogger<RadioViewModel> _logger;
    private CancellationTokenSource? _directoryCts;
    private CancellationTokenSource? _searchCts;
    private string? _directoryGenreId;
    private string? _currentStationId;
    private bool _isPlaying;

    public RadioViewModel(IRadioDirectory directory, IRadioFavorites favorites, IPlayer player, PageServices services, ILogger<RadioViewModel> logger)
        : base(services)
    {
        _directory = directory;
        _favorites = favorites;
        _player = player;
        _logger = logger;
        Genres = CuratedRadio.Genres;
        SelectedGenre = Genres.FirstOrDefault(g => g.Id == s_genreId) ?? Genres[0];
        ShowCurated();
        HasContent = true; // the curated list is built in
        SyncPlaying();
    }

    public IReadOnlyList<RadioGenre> Genres { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CuratedHeading), nameof(DirectoryHeading))]
    public partial RadioGenre SelectedGenre { get; set; }

    public ObservableCollection<StationItem> Favorites { get; } = [];

    public ObservableCollection<StationItem> Curated { get; } = [];

    public ObservableCollection<StationItem> Directory { get; } = [];

    public ObservableCollection<StationItem> Results { get; } = [];

    /// <summary>Directory search, debounced (two-way bound to the search box).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSearching), nameof(IsBrowsing), nameof(ResultsHeading))]
    public partial string Query { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasFavorites { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDirectory))]
    public partial bool IsDirectoryLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDirectoryError), nameof(ShowDirectory))]
    public partial string? DirectoryError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowResults))]
    public partial bool IsSearchLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSearchError), nameof(ShowResults))]
    public partial string? SearchError { get; set; }

    [ObservableProperty]
    public partial bool IsSearchEmpty { get; set; }

    public bool IsSearching => !string.IsNullOrWhiteSpace(Query);

    public bool IsBrowsing => !IsSearching;

    public bool HasDirectoryError => DirectoryError is not null;

    public bool ShowDirectory => !IsDirectoryLoading && DirectoryError is null;

    public bool HasSearchError => SearchError is not null;

    public bool ShowResults => !IsSearchLoading && SearchError is null;

    public string CuratedHeading => SelectedGenre.Name;

    public string DirectoryHeading => $"More {SelectedGenre.Name}";

    public string ResultsHeading => $"Stations matching “{Query.Trim()}”";

    /// <summary>A genre chip was picked (the chip's tag is the genre id).</summary>
    public void SelectGenre(string? genreId)
    {
        if (Genres.FirstOrDefault(g => g.Id == genreId) is not { } genre || genre == SelectedGenre)
        {
            return;
        }

        SelectedGenre = genre;
        ShowCurated();
        _ = LoadDirectoryAsync();
    }

    public async Task PlayAsync(StationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (list, source) = item.Section switch
        {
            StationSection.Favorites => (Favorites, new QueueSource(QueueSourceKind.LiveRadio, "favorites", "Favourite stations")),
            StationSection.Directory => (Directory, new QueueSource(QueueSourceKind.LiveRadio, SelectedGenre.Id, $"{SelectedGenre.Name} radio")),
            StationSection.Results => (Results, new QueueSource(QueueSourceKind.LiveRadio, "search", $"Stations matching “{Query.Trim()}”")),
            _ => (Curated, new QueueSource(QueueSourceKind.LiveRadio, SelectedGenre.Id, $"{SelectedGenre.Name} radio")),
        };

        var index = list.IndexOf(item);
        if (index < 0)
        {
            return;
        }

        // The whole section becomes the queue, so Next/Previous switch stations.
        await Actions.PlayTracksAsync([.. list.Select(s => s.Station.ToTrack())], index, source);
    }

    public async Task ToggleFavoriteAsync(StationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        try
        {
            await _favorites.SetAsync(item.Station, !item.IsFavorite);
        }
        catch (Exception ex)
        {
            ReportError("Couldn't update your favourite stations", ex);
        }
    }

    public async Task OpenWebsiteAsync(StationItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!Uri.TryCreate(item.Station.Homepage, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return;
        }

        try
        {
            if (!await Windows.System.Launcher.LaunchUriAsync(uri))
            {
                Notifications.ShowInfo("Couldn't open the website", uri.Host);
            }
        }
        catch (Exception ex)
        {
            ReportError("Couldn't open the website", ex);
        }
    }

    protected override async Task OnNavigatedToCoreAsync(object? parameter)
    {
        _favorites.Changed += OnFavoritesChanged;
        _player.TrackChanged += OnPlayerTrackChanged;
        _player.StatusChanged += OnPlayerStatusChanged;
        SyncPlaying();
        if (Query.Length == 0 && s_query.Length > 0)
        {
            Query = s_query;
        }

        if (_directoryGenreId != SelectedGenre.Id || HasDirectoryError)
        {
            _ = LoadDirectoryAsync();
        }

        try
        {
            await _favorites.LoadAsync(NavigationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the favourite stations");
        }

        SyncFavorites();
    }

    protected override void OnNavigatedFromCore()
    {
        _favorites.Changed -= OnFavoritesChanged;
        _player.TrackChanged -= OnPlayerTrackChanged;
        _player.StatusChanged -= OnPlayerStatusChanged;
    }

    // Retry on the error states.
    protected override Task LoadAsync() => IsSearching ? SearchAsync(Query) : LoadDirectoryAsync();

    [RelayCommand]
    private Task RetryDirectoryAsync() => LoadDirectoryAsync();

    [RelayCommand]
    private Task RetrySearchAsync() => SearchAsync(Query);

    partial void OnQueryChanged(string value)
    {
        s_query = value;
        _ = SearchAsync(value);
    }

    partial void OnSelectedGenreChanged(RadioGenre value) => s_genreId = value.Id;

    private void OnFavoritesChanged(object? sender, EventArgs e) => Services.Dispatcher.Run(SyncFavorites);

    private void OnPlayerTrackChanged(object? sender, TrackChangedEventArgs e) => Services.Dispatcher.Run(SyncPlaying);

    private void OnPlayerStatusChanged(object? sender, PlaybackStatusChangedEventArgs e) => Services.Dispatcher.Run(SyncPlaying);

    private void ShowCurated() => Replace(Curated, SelectedGenre.Stations, StationSection.Curated);

    private async Task LoadDirectoryAsync()
    {
        _directoryCts?.Cancel();
        var cts = new CancellationTokenSource();
        _directoryCts = cts;
        var genre = SelectedGenre;
        Directory.Clear();
        DirectoryError = null;
        IsDirectoryLoading = true;
        try
        {
            var stations = await _directory.GetByTagsAsync(genre.DirectoryTags, DirectoryLimit + genre.Stations.Count, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // Stations already in the curated grid above aren't repeated.
            var shown = genre.Stations.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            var names = genre.Stations.Select(s => s.Name.ToUpperInvariant()).ToHashSet(StringComparer.Ordinal);
            Replace(Directory, stations.Where(s => !shown.Contains(s.Id) && !names.Contains(s.Name.ToUpperInvariant())).Take(DirectoryLimit), StationSection.Directory);
            _directoryGenreId = genre.Id;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load {Genre} stations from the directory", genre.Name);
            DirectoryError = ex.Message;
            ReportError("Couldn't load more stations", ex);
        }
        finally
        {
            if (ReferenceEquals(_directoryCts, cts))
            {
                IsDirectoryLoading = false;
            }
        }
    }

    private async Task SearchAsync(string query)
    {
        _searchCts?.Cancel();
        query = query.Trim();
        if (query.Length == 0)
        {
            _searchCts = null;
            Results.Clear();
            IsSearchLoading = false;
            SearchError = null;
            IsSearchEmpty = false;
            return;
        }

        var cts = new CancellationTokenSource();
        _searchCts = cts;
        IsSearchLoading = true;
        SearchError = null;
        IsSearchEmpty = false;
        try
        {
            await Task.Delay(SearchDelay, cts.Token);
            var found = await _directory.SearchAsync(query, SearchLimit, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            // Curated stations that match (by name or genre) come first.
            var curated = Genres.SelectMany(g => g.Stations)
                .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || s.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))
                .DistinctBy(s => s.Id)
                .ToList();
            var ids = curated.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            Replace(Results, curated.Concat(found.Where(s => !ids.Contains(s.Id))).Take(SearchLimit), StationSection.Results);
            IsSearchEmpty = Results.Count == 0;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Radio search for {Query} failed", query);
            Results.Clear();
            SearchError = ex.Message;
            ReportError("Couldn't search radio stations", ex);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts))
            {
                IsSearchLoading = false;
            }
        }
    }

    private void Replace(ObservableCollection<StationItem> target, IEnumerable<RadioStation> stations, StationSection section)
    {
        target.Clear();
        foreach (var station in stations)
        {
            var item = new StationItem(station, section, this) { IsFavorite = _favorites.Contains(station.Id) };
            Mark(item);
            target.Add(item);
        }
    }

    private void SyncFavorites()
    {
        Replace(Favorites, _favorites.Items, StationSection.Favorites);
        HasFavorites = Favorites.Count > 0;
        foreach (var item in AllItems())
        {
            item.IsFavorite = _favorites.Contains(item.Id);
        }
    }

    private void SyncPlaying()
    {
        _currentStationId = _player.CurrentTrack?.Station?.Id;
        _isPlaying = _player.Status is PlaybackStatus.Playing or PlaybackStatus.Loading or PlaybackStatus.Buffering;
        foreach (var item in AllItems())
        {
            Mark(item);
        }
    }

    private void Mark(StationItem item)
    {
        item.IsCurrent = item.Id == _currentStationId;
        item.IsPlaying = item.IsCurrent && _isPlaying;
    }

    private IEnumerable<StationItem> AllItems() => Favorites.Concat(Curated).Concat(Directory).Concat(Results);
}
