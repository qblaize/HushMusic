using System.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class LikedSongsViewModel : SignedInPageViewModelBase, ITrackListHost, ITrackSelectionHost
{
    // YouTube Music's id for the "Liked music" auto playlist.
    private const string LikedMusicPlaylistId = "LM";

    private static readonly PropertyChangedEventArgs ShowProgressArgs = new(nameof(ShowProgress));

    private readonly ILibraryApi _library;
    private readonly IQueueService _queue;

    public LikedSongsViewModel(ILibraryApi library, IQueueService queue, PageServices services)
        : base(services)
    {
        _library = library;
        _queue = queue;
        Tracks = TrackItem.CreateList((c, ct) => _library.GetLikedSongsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
        Filter = new TrackListFilter(Tracks, () => NavigationToken, TrackListOwnOrder.LikedSongs);
        Selection = new TrackSelection(Filter.Rows, services.Actions, () => Source);
        Filter.Arranged += (_, _) => Selection.Forget(Tracks.Except(Filter.Rows));
        Filter.PropertyChanged += OnFilterPropertyChanged;
    }

    /// <summary>Every liked song, newest first.</summary>
    public IncrementalCollection<TrackItem> Tracks { get; }

    /// <summary>The filter and sort above the list; its <see cref="TrackListFilter.Rows"/> are what the list shows.</summary>
    public TrackListFilter Filter { get; }

    public TrackSelection Selection { get; }

    /// <summary>The thin progress bar: a reload, or the rest of the list loading for the filter or sort.</summary>
    public bool ShowProgress => ShowBusyBar || Filter.IsLoadingRest;

    private static QueueSource Source => new(QueueSourceKind.LikedSongs, LikedMusicPlaylistId, "Liked songs");

    protected override string LoadErrorTitle => "Couldn't load your liked songs";

    public Task PlayFromTrackAsync(Track track)
    {
        var tracks = Filter.Rows.Select(t => t.Track).ToList();
        var index = tracks.FindIndex(t => ReferenceEquals(t, track));
        return index < 0
            ? Task.CompletedTask
            : Actions.PlayTracksAsync(tracks, index, Source);
    }

    public bool CanRemoveFromPlaylist(Track track) => false;

    public Task RemoveFromPlaylistAsync(Track track) => Task.CompletedTask;

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        Services.Likes.Changed += OnTrackRated;
        return base.OnNavigatedToCoreAsync(parameter);
    }

    protected override void OnNavigatedFromCore()
    {
        Services.Likes.Changed -= OnTrackRated;
        Selection.Exit();
        base.OnNavigatedFromCore();
    }

    protected override async Task LoadContentAsync(CancellationToken cancellationToken)
    {
        var page = await _library.GetLikedSongsAsync(null, cancellationToken);
        Tracks.Reset(TrackItem.From(page.Items), page.Continuation);
        IsEmpty = Tracks.Count == 0;
        Services.Warmup.Warm(page.Items.FirstOrDefault(t => t.IsAvailable));
    }

    protected override void ClearContent() => Tracks.Reset([], null);

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(HasContent))
        {
            base.OnPropertyChanged(ShowProgressArgs);
        }
    }

    /// <summary>Plays all liked songs, or while they are filtered or sorted the songs shown, in that order.</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (!Filter.IsActive)
        {
            await Actions.PlayPlaylistAsync(LikedMusicPlaylistId);
            return;
        }

        await Filter.WhenCompleteAsync();
        if (Filter.Rows.Count > 0 && !NavigationToken.IsCancellationRequested)
        {
            await Actions.PlayTracksAsync([.. Filter.Rows.Select(t => t.Track)], 0, Source);
        }
    }

    [RelayCommand]
    private async Task ShuffleAsync()
    {
        if (!Filter.IsActive)
        {
            await Actions.PlayPlaylistAsync(LikedMusicPlaylistId, shuffle: true);
            return;
        }

        await Filter.WhenCompleteAsync();
        List<Track> tracks = [.. Filter.Rows.Select(t => t.Track).Where(t => t.IsAvailable)];
        if (tracks.Count > 0 && !NavigationToken.IsCancellationRequested)
        {
            await Actions.PlayTracksAsync(tracks, Random.Shared.Next(tracks.Count), Source);
            if (_queue.Source == Source && !_queue.IsShuffled)
            {
                _queue.SetShuffle(true);
            }
        }
    }

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayFromTrackAsync(item.Track);

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackListFilter.IsLoadingRest))
        {
            OnPropertyChanged(ShowProgressArgs);
        }
    }

    // Unliking a song anywhere in the app removes it from this list right away.
    private void OnTrackRated(object? sender, TrackRatedEventArgs e)
    {
        if (e.Status == LikeStatus.Like)
        {
            return;
        }

        for (var i = Tracks.Count - 1; i >= 0; i--)
        {
            if (Tracks[i].Track.VideoId == e.VideoId)
            {
                Selection.Forget([Tracks[i]]);
                Tracks.RemoveAt(i);
            }
        }

        IsEmpty = HasContent && Tracks.Count == 0 && !Tracks.HasMoreItems;
    }
}
