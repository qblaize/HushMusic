using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.NowPlaying;

/// <summary>
/// The "Related" tab: the player's related shelves for the current track (ytmusicapi <c>get_song_related</c>).
/// Loads only while the tab is visible; a track change cancels the request in flight.
/// </summary>
public sealed partial class RelatedViewModel : ObservableObject
{
    private const int MaxSongs = 10;
    private const int MaxCards = 6;
    private const string NothingPlaying = "Play a song to see related music.";

    private readonly IWatchApi _watch;
    private readonly TrackBrowseIds _browseIds;
    private readonly IMediaItemActions _actions;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<RelatedViewModel> _logger;
    private CancellationTokenSource? _cts;
    private Track? _track;
    private string? _shownVideoId;
    private bool _isActive;

    public RelatedViewModel(
        IPlayer player,
        IWatchApi watch,
        TrackBrowseIds browseIds,
        IMediaItemActions actions,
        IUiDispatcher dispatcher,
        ILogger<RelatedViewModel> logger)
    {
        _watch = watch;
        _browseIds = browseIds;
        _actions = actions;
        _dispatcher = dispatcher;
        _logger = logger;
        player.TrackChanged += (_, e) => _dispatcher.Run(() => OnTrackChanged(e.Track));
        _track = player.CurrentTrack;
        Message = _track is null ? NothingPlaying : null;
    }

    /// <summary>An item is about to open a page (Now Playing closes first).</summary>
    public event EventHandler? Navigating;

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShelves))]
    public partial IReadOnlyList<RelatedShelfViewModel> Shelves { get; set; } = [];

    /// <summary>Empty-state text; null while shelves, loading or an error are shown.</summary>
    [ObservableProperty]
    public partial string? Message { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasShelves => Shelves.Count > 0;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The related tab became visible (true) or hidden (false).</summary>
    public void SetActive(bool active)
    {
        _isActive = active;
        if (active)
        {
            EnsureLoaded();
        }
    }

    [RelayCommand]
    private void Retry()
    {
        _shownVideoId = null;
        EnsureLoaded();
    }

    private void OnTrackChanged(Track? track)
    {
        if (track?.VideoId == _track?.VideoId && track is not null)
        {
            return;
        }

        _track = track;
        _cts?.Cancel();
        _shownVideoId = null;
        if (_isActive)
        {
            EnsureLoaded();
        }
        else
        {
            Show([], track is null ? NothingPlaying : null);
        }
    }

    private void EnsureLoaded()
    {
        var track = _track;
        if (track is null)
        {
            _cts?.Cancel();
            IsLoading = false;
            Show([], NothingPlaying);
            return;
        }

        if (_shownVideoId == track.VideoId)
        {
            return;
        }

        if (track.IsLiveRadio)
        {
            // Live radio is not on YouTube: nothing is related to it there.
            _cts?.Cancel();
            IsLoading = false;
            _shownVideoId = track.VideoId;
            Show([], "Nothing related for live radio.");
            return;
        }

        _ = LoadAsync(track);
    }

    private async Task LoadAsync(Track track)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;

        Show([], null);
        IsLoading = true;
        try
        {
            var watch = await _browseIds.GetAsync(track.VideoId, cts.Token);
            IReadOnlyList<Shelf> shelves = watch.RelatedBrowseId is { Length: > 0 } browseId
                ? await _watch.GetRelatedAsync(browseId, cts.Token)
                : [];
            if (cts.IsCancellationRequested)
            {
                return;
            }

            _shownVideoId = track.VideoId;
            var models = shelves
                .Select(Map)
                .Where(s => s.HasSongs || s.HasCards || s.HasDescription)
                .ToList();
            Show(models, models.Count == 0 ? "Nothing related to this song yet." : null);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load related music for {VideoId}", track.VideoId);
            ErrorMessage = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                IsLoading = false;
            }
        }
    }

    private void Show(IReadOnlyList<RelatedShelfViewModel> shelves, string? message)
    {
        Shelves = shelves;
        Message = message;
        ErrorMessage = null;
    }

    private RelatedShelfViewModel Map(Shelf shelf)
    {
        var songs = shelf.Items.OfType<Track>().Take(MaxSongs).Select(t => new RelatedSongViewModel(t, PlayAsync)).ToList();
        var cards = shelf.Items.Where(i => i is not Track).Take(MaxCards).Select(i => new RelatedCardViewModel(i, Open)).ToList();

        // "About the artist" comes as a shelf with a description and no items.
        var description = songs.Count == 0 && cards.Count == 0 ? shelf.Subtitle : null;
        return new RelatedShelfViewModel(shelf.Title, description, songs, cards);
    }

    private Task PlayAsync(Track track) => _actions.PlayTrackAsync(track);

    private void Open(MediaItem item)
    {
        // Mixes have no page: they start playing and Now Playing stays open.
        if (item is not Playlist { IsMix: true })
        {
            Navigating?.Invoke(this, EventArgs.Empty);
        }

        _actions.Open(item);
    }
}

public sealed class RelatedShelfViewModel(
    string title,
    string? description,
    IReadOnlyList<RelatedSongViewModel> songs,
    IReadOnlyList<RelatedCardViewModel> cards)
{
    public string Title { get; } = title;

    public string? Description { get; } = description;

    public IReadOnlyList<RelatedSongViewModel> Songs { get; } = songs;

    public IReadOnlyList<RelatedCardViewModel> Cards { get; } = cards;

    public bool HasSongs => Songs.Count > 0;

    public bool HasCards => Cards.Count > 0;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

/// <summary>A song row in the related tab: plays it (with up next).</summary>
public sealed class RelatedSongViewModel
{
    public RelatedSongViewModel(Track track, Func<Track, Task> play)
    {
        Title = track.Title;
        Subtitle = ItemFormat.TrackSubtitle(track, isAlbumRow: false);
        ArtUrl = ItemFormat.RowArt(track);
        PlayCommand = new AsyncRelayCommand(() => play(track));
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string? ArtUrl { get; }

    public IAsyncRelayCommand PlayCommand { get; }
}

/// <summary>An album, playlist or artist card in the related tab: opens its page.</summary>
public sealed class RelatedCardViewModel
{
    public RelatedCardViewModel(MediaItem item, Action<MediaItem> open)
    {
        Title = item.Title;
        Subtitle = ItemFormat.CardSubtitle(item);
        ArtUrl = item.ThumbnailFor(226)?.Url;
        IsRound = item is Artist;
        PlaceholderGlyph = ItemFormat.PlaceholderGlyph(item);
        AutomationName = $"{ItemFormat.Kind(item)}: {item.Title}";
        OpenCommand = new RelayCommand(() => open(item));
    }

    public string Title { get; }

    public string Subtitle { get; }

    public string? ArtUrl { get; }

    public bool IsRound { get; }

    public string PlaceholderGlyph { get; }

    public string AutomationName { get; }

    public IRelayCommand OpenCommand { get; }
}
