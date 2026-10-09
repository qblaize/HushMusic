using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

/// <summary>
/// The Explore tab: new releases, the trending (or, for Premium accounts, top) songs, moods and genres, the charts of a
/// country and new music videos.
/// </summary>
public sealed partial class ExploreViewModel : PageViewModelBase
{
    // Going Back keeps what is on screen unless it is older than this.
    private static readonly TimeSpan MaxAgeOnBack = TimeSpan.FromMinutes(10);

    private const int ChartRows = 10;
    private const int MoodTiles = 12;
    private const int ChartArtists = 20;

    // The page isn't kept for the whole session (see NavigationService), so the chart country picked last outlives it.
    private static string? s_chartCountry;

    private readonly IExploreApi _explore;
    private readonly ISettingsService _settings;
    private IReadOnlyList<Track> _chartTracks = [];
    private CancellationTokenSource? _chartsCts;
    private DateTimeOffset _loadedAt;
    private AuthStatus _loadedFor;
    private bool _accountChanged;
    private bool _isActive;
    private bool _settingCountry;

    public ExploreViewModel(IExploreApi explore, ISettingsService settings, PageServices services)
        : base(services)
    {
        _explore = explore;
        _settings = settings;
    }

    public ObservableCollection<Album> NewReleases { get; } = [];

    /// <summary>The first rows of the song chart, numbered by their chart position.</summary>
    public ObservableCollection<TrackItem> ChartTracks { get; } = [];

    public ObservableCollection<MoodCategory> Moods { get; } = [];

    public ObservableCollection<Track> NewVideos { get; } = [];

    public ObservableCollection<CountryOption> Countries { get; } = [];

    public ObservableCollection<Artist> TopArtists { get; } = [];

    public ObservableCollection<Shelf> PlaylistCharts { get; } = [];

    /// <summary>"Trending" or "Top songs".</summary>
    [ObservableProperty]
    public partial string ChartTitle { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SeeAllChartSongsCommand))]
    public partial string? ChartPlaylistId { get; set; }

    [ObservableProperty]
    public partial bool HasChartTracks { get; set; }

    [ObservableProperty]
    public partial bool HasNewReleases { get; set; }

    [ObservableProperty]
    public partial bool HasMoods { get; set; }

    [ObservableProperty]
    public partial bool HasNewVideos { get; set; }

    /// <summary>The charts loaded at least once (the section stays while another country loads).</summary>
    [ObservableProperty]
    public partial bool HasCharts { get; set; }

    [ObservableProperty]
    public partial bool HasTopArtists { get; set; }

    [ObservableProperty]
    public partial bool IsChartsBusy { get; set; }

    /// <summary>Position of the chart country in <see cref="Countries"/>; -1 before the charts load.</summary>
    [ObservableProperty]
    public partial int SelectedCountryIndex { get; set; } = -1;

    public CountryOption? SelectedCountry => SelectedCountryIndex >= 0 && SelectedCountryIndex < Countries.Count ? Countries[SelectedCountryIndex] : null;

    /// <summary>The page should scroll to the top: this visit shows freshly loaded content.</summary>
    public bool IsFreshVisit { get; private set; }

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        // Signing in can add the Premium "Top songs" chart and changes what YouTube Music recommends. Subscribed only
        // while shown, so the page can be let go of; a change while away is caught by comparing the status.
        _isActive = true;
        Services.Auth.StatusChanged += OnAuthStatusChanged;
        _accountChanged |= HasContent && Services.Auth.Status != _loadedFor;
        IsFreshVisit = !HasContent || !IsBackNavigation || _accountChanged || DateTimeOffset.UtcNow - _loadedAt > MaxAgeOnBack;
        return IsFreshVisit ? LoadAsync() : Task.CompletedTask;
    }

    protected override void OnNavigatedFromCore()
    {
        _isActive = false;
        Services.Auth.StatusChanged -= OnAuthStatusChanged;
        _chartsCts?.Cancel();
    }

    protected override Task LoadAsync() => RunAsync(
        async ct =>
        {
            // The charts are a separate request, loaded alongside: when they fail the rest of the page still shows.
            var charts = LoadChartsAsync(SelectedCountry?.Code ?? s_chartCountry ?? DefaultCountry(), ct);
            _loadedFor = Services.Auth.Status;
            ShowExplore(await _explore.GetExploreAsync(ct));
            _loadedAt = DateTimeOffset.UtcNow;
            _accountChanged = false;
            HasContent = true;
            await charts;
            IsEmpty = !HasNewReleases && !HasChartTracks && !HasMoods && !HasNewVideos && !HasCharts;
        },
        "Couldn't load Explore");

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync();

    [RelayCommand]
    private void SeeAllNewReleases() => OpenCategory(new NewReleasesRequest());

    [RelayCommand]
    private void SeeAllNewVideos() => OpenCategory(new NewVideosRequest());

    [RelayCommand]
    private void SeeAllMoods() => OpenCategory(new MoodsAndGenresRequest());

    [RelayCommand]
    private void SeeAllCharts() => OpenCategory(new ChartsRequest(SelectedCountry?.Code ?? DefaultCountry()));

    [RelayCommand(CanExecute = nameof(CanSeeAllChartSongs))]
    private void SeeAllChartSongs() => Actions.OpenPlaylist(ChartPlaylistId);

    [RelayCommand]
    private void OpenMood(MoodCategory? category)
    {
        if (category is not null)
        {
            OpenCategory(new MoodRequest(category.Title, category.Params, category.Color));
        }
    }

    /// <summary>Plays the chart from the clicked song, in chart order.</summary>
    [RelayCommand]
    private Task PlayChartTrackAsync(TrackItem? item)
    {
        if (item is null)
        {
            return Task.CompletedTask;
        }

        var index = ChartTracks.IndexOf(item);
        return Actions.PlayTracksAsync(_chartTracks, Math.Max(0, index), new QueueSource(QueueSourceKind.Playlist, ChartPlaylistId, ChartTitle));
    }

    partial void OnSelectedCountryIndexChanged(int value)
    {
        if (!_settingCountry && HasCharts && SelectedCountry is { } country)
        {
            s_chartCountry = country.Code;
            _ = ReloadChartsAsync(country.Code);
        }
    }

    private void OnAuthStatusChanged(object? sender, AuthStatusChangedEventArgs e) => Services.Dispatcher.Run(() =>
    {
        _accountChanged = true;
        if (_isActive)
        {
            _ = LoadAsync();
        }
    });

    private bool CanSeeAllChartSongs() => ChartPlaylistId is not null;

    private void OpenCategory(ExploreCategoryRequest request) => Services.Navigation.NavigateTo(PageKey.ExploreCategory, request);

    // The charts follow Settings → Content location when it names a chart country; otherwise they start Global.
    private string DefaultCountry() =>
        _settings.Current.ContentLocation is { Length: 2 } location ? location.ToUpperInvariant() : CountryOption.GlobalCode;

    private void ShowExplore(ExplorePage page)
    {
        Replace(NewReleases, page.NewReleases);
        HasNewReleases = NewReleases.Count > 0;

        var chart = page.TopSongs ?? page.Trending;
        _chartTracks = chart?.Entries.Select(e => e.Item).ToList() ?? [];
        ChartTitle = chart?.Title is { Length: > 0 } title ? title : "Trending";
        ChartPlaylistId = chart?.PlaylistId;
        Replace(ChartTracks, chart?.Entries.Take(ChartRows).Select((e, i) => new TrackItem(e.Item, e.Rank ?? i + 1)) ?? []);
        HasChartTracks = ChartTracks.Count > 0;

        Replace(Moods, page.MoodsAndGenres.Take(MoodTiles));
        HasMoods = Moods.Count > 0;

        Replace(NewVideos, page.NewVideos);
        HasNewVideos = NewVideos.Count > 0;
    }

    // Another country from the picker: only the charts reload.
    private async Task ReloadChartsAsync(string country)
    {
        _chartsCts?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(NavigationToken);
        _chartsCts = cts;
        IsChartsBusy = true;
        try
        {
            await LoadChartsAsync(country, cts.Token);
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

    /// <summary>Never throws: a failure is reported as an InfoBar and the previous charts stay.</summary>
    private async Task LoadChartsAsync(string country, CancellationToken cancellationToken)
    {
        try
        {
            await LoadChartsCoreAsync(country, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            ReportError("Couldn't load the charts", ex);
        }
    }

    private async Task LoadChartsCoreAsync(string country, CancellationToken cancellationToken)
    {
        var charts = await _explore.GetChartsAsync(country, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        _settingCountry = true;
        try
        {
            var countries = CountryOption.Sorted(charts.Countries);
            if (countries.Count > 0 && !countries.SequenceEqual(Countries))
            {
                Replace(Countries, countries);
            }

            var selected = Countries.FirstOrDefault(c => string.Equals(c.Code, country, StringComparison.OrdinalIgnoreCase))
                ?? Countries.FirstOrDefault(c => string.Equals(c.Name, charts.CountryName, StringComparison.OrdinalIgnoreCase));
            SelectedCountryIndex = selected is null ? -1 : Countries.IndexOf(selected);
        }
        finally
        {
            _settingCountry = false;
        }

        Replace(TopArtists, charts.TopArtists.Take(ChartArtists).Select(e => e.Item));
        HasTopArtists = TopArtists.Count > 0;
        Replace(PlaylistCharts, charts.PlaylistCharts.Where(s => s.Items.Count > 0));
        HasCharts = HasTopArtists || PlaylistCharts.Count > 0;
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
