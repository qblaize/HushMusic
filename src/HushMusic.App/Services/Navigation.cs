using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;

namespace HushMusic.App.Services;

public enum PageKey
{
    Home,
    Search,
    Library,
    LikedSongs,
    Playlists,
    History,
    Settings,

    /// <summary>Parameter: album browse id (string).</summary>
    Album,

    /// <summary>Parameter: artist channel id (string).</summary>
    Artist,

    /// <summary>Parameter: playlist id (string).</summary>
    Playlist,

    /// <summary>Live internet radio: genres, favourites, directory search.</summary>
    Radio,

    /// <summary>Moods &amp; genres, charts and new releases.</summary>
    Explore,

    /// <summary>Parameter: the category to show (moods/genres playlists, a chart, new releases).</summary>
    ExploreCategory,

    /// <summary>Parameter: the artist's albums / singles / videos "See all" request.</summary>
    ArtistDiscography,

    /// <summary>Local listening statistics.</summary>
    Stats,
}

/// <summary>Maps <see cref="PageKey"/> to page types. Filled by the Shell/Page service registrations.</summary>
public sealed class PageRegistry
{
    private readonly Dictionary<PageKey, Type> _pages = [];
    private readonly Dictionary<Type, PageKey> _keys = [];

    public void Register<TPage>(PageKey key)
        where TPage : Page
    {
        _pages[key] = typeof(TPage);
        _keys[typeof(TPage)] = key;
    }

    public Type Resolve(PageKey key) =>
        _pages.TryGetValue(key, out var type) ? type : throw new InvalidOperationException($"No page registered for {key}.");

    public PageKey? KeyOf(Type pageType) => _keys.TryGetValue(pageType, out var key) ? key : null;
}

public interface INavigationService
{
    bool CanGoBack { get; }

    PageKey? CurrentPage { get; }

    /// <summary>Raised after every navigation (including back).</summary>
    event EventHandler<PageKey?>? Navigated;

    void Initialize(Frame frame);

    bool NavigateTo(PageKey page, object? parameter = null);

    void GoBack();
}

public sealed class NavigationService(PageRegistry registry, ILogger<NavigationService> logger) : INavigationService
{
    private Frame? _frame;
    private PageKey? _currentKey;
    private object? _currentParameter;

    public bool CanGoBack => _frame?.CanGoBack == true;

    public PageKey? CurrentPage => _currentKey;

    public event EventHandler<PageKey?>? Navigated;

    public void Initialize(Frame frame)
    {
        _frame = frame;
        _frame.Navigated += OnNavigated;
        _frame.NavigationFailed += OnNavigationFailed;
    }

    public bool NavigateTo(PageKey page, object? parameter = null)
    {
        if (_frame is null)
        {
            return false;
        }

        // The placeholder page is shared by several keys, so compare keys rather than page types.
        if (_currentKey == page && Equals(_currentParameter, parameter))
        {
            return false;
        }

        _currentKey = page;
        _currentParameter = parameter;
        return _frame.Navigate(registry.Resolve(page), parameter, new EntranceNavigationTransitionInfo());
    }

    public void GoBack()
    {
        if (_frame?.CanGoBack == true)
        {
            _frame.GoBack();
        }
    }

    private void OnNavigated(object sender, NavigationEventArgs e)
    {
        if (e.NavigationMode == NavigationMode.Back)
        {
            _currentKey = registry.KeyOf(e.SourcePageType);
            _currentParameter = e.Parameter;
        }

        Navigated?.Invoke(this, _currentKey);
    }

    private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
    {
        logger.LogError(e.Exception, "Navigation to {Page} failed", e.SourcePageType.FullName);
        e.Handled = true;
    }
}
