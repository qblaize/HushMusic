using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public enum LibraryTab
{
    Playlists,
    Songs,
    Albums,
    Artists,
}

public sealed partial class LibraryViewModel : SignedInPageViewModelBase, ITrackListHost, ITrackSelectionHost
{
    private static readonly PropertyChangedEventArgs IsTabLoadingArgs = new(nameof(IsTabLoading));
    private static readonly PropertyChangedEventArgs ShowCardSkeletonArgs = new(nameof(ShowCardSkeleton));
    private static readonly PropertyChangedEventArgs ShowRowSkeletonArgs = new(nameof(ShowRowSkeleton));
    private static readonly PropertyChangedEventArgs ShowTabBusyBarArgs = new(nameof(ShowTabBusyBar));
    private static readonly PropertyChangedEventArgs CanSelectSongsArgs = new(nameof(CanSelectSongs));

    private readonly ILibraryApi _library;
    private readonly HashSet<LibraryTab> _loadedTabs = [];

    public LibraryViewModel(ILibraryApi library, PageServices services)
        : base(services)
    {
        _library = library;
        Playlists = new IncrementalCollection<Playlist>((c, ct) => _library.GetPlaylistsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
        Songs = TrackItem.CreateList((c, ct) => _library.GetSongsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
        Albums = new IncrementalCollection<Album>((c, ct) => _library.GetAlbumsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
        Artists = new IncrementalCollection<Artist>((c, ct) => _library.GetArtistsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
        Selection = new TrackSelection(Songs, services.Actions, () => SongsSource);
    }

    public IncrementalCollection<Playlist> Playlists { get; }

    public IncrementalCollection<TrackItem> Songs { get; }

    public IncrementalCollection<Album> Albums { get; }

    public IncrementalCollection<Artist> Artists { get; }

    /// <summary>Multi-select on the Songs tab.</summary>
    public TrackSelection Selection { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPlaylistsTab))]
    [NotifyPropertyChangedFor(nameof(IsSongsTab))]
    [NotifyPropertyChangedFor(nameof(IsAlbumsTab))]
    [NotifyPropertyChangedFor(nameof(IsArtistsTab))]
    [NotifyPropertyChangedFor(nameof(EmptyMessage))]
    [NotifyPropertyChangedFor(nameof(CanSelectSongs))]
    public partial LibraryTab SelectedTab { get; set; }

    public bool IsPlaylistsTab => SelectedTab == LibraryTab.Playlists;

    public bool IsSongsTab => SelectedTab == LibraryTab.Songs;

    public bool IsAlbumsTab => SelectedTab == LibraryTab.Albums;

    public bool IsArtistsTab => SelectedTab == LibraryTab.Artists;

    /// <summary>The Songs tab shows its "Select" button.</summary>
    public bool CanSelectSongs => IsSongsTab && !RequiresSignIn;

    /// <summary>The selected tab is loading for the first time (its list is still empty): show placeholders.</summary>
    public bool IsTabLoading => IsBusy && !RequiresSignIn && !_loadedTabs.Contains(SelectedTab);

    public bool ShowCardSkeleton => IsTabLoading && !IsSongsTab;

    public bool ShowRowSkeleton => IsTabLoading && IsSongsTab;

    /// <summary>The top busy bar, for reloads of a tab that already shows content.</summary>
    public bool ShowTabBusyBar => ShowBusyBar && !IsTabLoading;

    public string EmptyMessage => SelectedTab switch
    {
        LibraryTab.Songs => "Songs you add to your library show up here.",
        LibraryTab.Albums => "Albums you add to your library show up here.",
        LibraryTab.Artists => "Artists you subscribe to show up here.",
        _ => "Playlists you create or save show up here.",
    };

    protected override string LoadErrorTitle => "Couldn't load your library";

    public Task PlayFromTrackAsync(Track track)
    {
        var tracks = Songs.Select(s => s.Track).ToList();
        var index = tracks.FindIndex(t => ReferenceEquals(t, track));
        return index < 0
            ? Task.CompletedTask
            : Actions.PlayTracksAsync(tracks, index, SongsSource);
    }

    private static QueueSource SongsSource => new(QueueSourceKind.Library, null, "Library songs");

    protected override void OnNavigatedFromCore()
    {
        Selection.Exit();
        base.OnNavigatedFromCore();
    }

    public bool CanRemoveFromPlaylist(Track track) => false;

    public Task RemoveFromPlaylistAsync(Track track) => Task.CompletedTask;

    protected override async Task LoadContentAsync(CancellationToken cancellationToken)
    {
        var tab = SelectedTab;
        switch (tab)
        {
            case LibraryTab.Playlists:
                var playlists = await _library.GetPlaylistsAsync(null, cancellationToken);
                Playlists.Reset(playlists.Items, playlists.Continuation);
                break;
            case LibraryTab.Songs:
                var songs = await _library.GetSongsAsync(null, cancellationToken);
                Songs.Reset(TrackItem.From(songs.Items), songs.Continuation);
                break;
            case LibraryTab.Albums:
                var albums = await _library.GetAlbumsAsync(null, cancellationToken);
                Albums.Reset(albums.Items, albums.Continuation);
                break;
            case LibraryTab.Artists:
                var artists = await _library.GetArtistsAsync(null, cancellationToken);
                Artists.Reset(artists.Items, artists.Continuation);
                break;
        }

        _loadedTabs.Add(tab);
        UpdateIsEmpty();
    }

    protected override void ClearContent()
    {
        _loadedTabs.Clear();
        Playlists.Reset([], null);
        Songs.Reset([], null);
        Albums.Reset([], null);
        Artists.Reset([], null);
    }

    [RelayCommand]
    private Task SelectTabAsync(LibraryTab tab)
    {
        if (tab == SelectedTab && _loadedTabs.Contains(tab))
        {
            return Task.CompletedTask;
        }

        Selection.Exit();
        SelectedTab = tab;
        if (_loadedTabs.Contains(tab))
        {
            UpdateIsEmpty();
            return Task.CompletedTask;
        }

        IsEmpty = false;
        return LoadAsync();
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(RequiresSignIn))
        {
            base.OnPropertyChanged(CanSelectSongsArgs);
        }

        if (e.PropertyName is nameof(IsBusy) or nameof(SelectedTab) or nameof(RequiresSignIn) or nameof(HasContent))
        {
            base.OnPropertyChanged(IsTabLoadingArgs);
            base.OnPropertyChanged(ShowCardSkeletonArgs);
            base.OnPropertyChanged(ShowRowSkeletonArgs);
            base.OnPropertyChanged(ShowTabBusyBarArgs);
        }
    }

    [RelayCommand]
    private Task PlaySongAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayFromTrackAsync(item.Track);

    /// <summary>"Your stats": local listening statistics (no account needed).</summary>
    [RelayCommand]
    private void OpenStats() => Services.Navigation.NavigateTo(PageKey.Stats);

    private void UpdateIsEmpty()
    {
        var count = SelectedTab switch
        {
            LibraryTab.Songs => Songs.Count,
            LibraryTab.Albums => Albums.Count,
            LibraryTab.Artists => Artists.Count,
            _ => Playlists.Count,
        };
        IsEmpty = _loadedTabs.Contains(SelectedTab) && count == 0;
    }
}
