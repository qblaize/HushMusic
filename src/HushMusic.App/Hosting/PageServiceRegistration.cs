using Microsoft.Extensions.DependencyInjection;
using HushMusic.App.Services.Pages;
using HushMusic.App.Services.Radio;
using HushMusic.App.ViewModels.Pages;
using HushMusic.App.Views.Pages;

namespace HushMusic.App.Hosting;

// Content pages, their view models and the shared item services.
internal static class PageServiceRegistration
{
    public static IServiceCollection AddPageServices(this IServiceCollection services, PageRegistry pages)
    {
        services.AddSingleton<ILikeStateService, LikeStateService>();
        services.AddSingleton<INowPlayingService, NowPlayingService>();
        services.AddSingleton<IPlaylistDialogService, PlaylistDialogService>();
        services.AddSingleton<IPlaylistAdder, PlaylistAdder>();
        services.AddSingleton<IMediaItemActions, MediaItemActions>();
        services.AddSingleton<IStreamWarmup, StreamWarmup>();
        services.AddSingleton<INavigationPreviews, NavigationPreviews>();
        services.AddSingleton<IRecentSearches, RecentSearches>();
        services.AddSingleton<IPlaylistDropTarget, PlaylistDropTarget>();
        services.AddSingleton<IStationLogos, StationLogos>();
        services.AddSingleton<IConfirmDialogService, ConfirmDialogService>();
        services.AddSingleton<PageServices>();

        Register<HomePage, HomeViewModel>(PageKey.Home);
        Register<SearchPage, SearchViewModel>(PageKey.Search);
        Register<LibraryPage, LibraryViewModel>(PageKey.Library);
        Register<LikedSongsPage, LikedSongsViewModel>(PageKey.LikedSongs);
        Register<PlaylistsPage, PlaylistsViewModel>(PageKey.Playlists);
        Register<HistoryPage, HistoryViewModel>(PageKey.History);
        Register<AlbumDetailPage, AlbumViewModel>(PageKey.Album);
        Register<ArtistDetailPage, ArtistViewModel>(PageKey.Artist);
        Register<PlaylistDetailPage, PlaylistViewModel>(PageKey.Playlist);
        Register<RadioPage, RadioViewModel>(PageKey.Radio);
        Register<ExplorePage, ExploreViewModel>(PageKey.Explore);
        Register<ExploreCategoryPage, ExploreCategoryViewModel>(PageKey.ExploreCategory);
        Register<ArtistDiscographyPage, ArtistDiscographyViewModel>(PageKey.ArtistDiscography);
        Register<StatsPage, StatsViewModel>(PageKey.Stats);

        return services;

        void Register<TPage, TViewModel>(PageKey key)
            where TPage : Page
            where TViewModel : class
        {
            pages.Register<TPage>(key);
            services.AddTransient<TViewModel>();
        }
    }
}
