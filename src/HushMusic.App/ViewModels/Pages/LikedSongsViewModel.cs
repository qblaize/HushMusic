using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class LikedSongsViewModel : SignedInPageViewModelBase, ITrackListHost, ITrackSelectionHost
{
    // YouTube Music's id for the "Liked music" auto playlist.
    private const string LikedMusicPlaylistId = "LM";

    private readonly ILibraryApi _library;

    public LikedSongsViewModel(ILibraryApi library, PageServices services)
        : base(services)
    {
        _library = library;
        Tracks = TrackItem.CreateList((c, ct) => _library.GetLikedSongsAsync(c, ct), HandleLoadMoreError, () => NavigationToken);
        Selection = new TrackSelection(Tracks, services.Actions, () => Source);
    }

    public IncrementalCollection<TrackItem> Tracks { get; }

    public TrackSelection Selection { get; }

    private static QueueSource Source => new(QueueSourceKind.LikedSongs, LikedMusicPlaylistId, "Liked songs");

    protected override string LoadErrorTitle => "Couldn't load your liked songs";

    public Task PlayFromTrackAsync(Track track)
    {
        var tracks = Tracks.Select(t => t.Track).ToList();
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

    [RelayCommand]
    private Task PlayAsync() => Actions.PlayPlaylistAsync(LikedMusicPlaylistId);

    [RelayCommand]
    private Task ShuffleAsync() => Actions.PlayPlaylistAsync(LikedMusicPlaylistId, shuffle: true);

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayFromTrackAsync(item.Track);

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
