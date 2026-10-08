using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;
using HushMusic.Core;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.RadioMatch;

public enum RadioMatchState
{
    Loading,
    Found,
    NoMatch,
    Failed,
}

/// <summary>
/// The "On YouTube Music" flyout for a song heard on a live station: the closest YouTube Music songs
/// (<see cref="IRadioTrackMatcher"/>) with play, play next, add to playlist and like. One per opened flyout; call
/// <see cref="Close"/> when it closes.
/// </summary>
public sealed partial class RadioMatchViewModel : ObservableObject
{
    private readonly IRadioTrackMatcher _matcher;
    private readonly IMediaItemActions _actions;
    private readonly ILikeStateService _likes;
    private readonly IStreamWarmup _warmup;
    private readonly INavigationService _navigation;
    private readonly ILogger<RadioMatchViewModel> _logger;
    private CancellationTokenSource? _cts;
    private RadioNowPlaying? _song;
    private string? _stationName;

    public RadioMatchViewModel(
        IRadioTrackMatcher matcher,
        IMediaItemActions actions,
        ILikeStateService likes,
        IStreamWarmup warmup,
        INavigationService navigation,
        ILogger<RadioMatchViewModel> logger)
    {
        _matcher = matcher;
        _actions = actions;
        _likes = likes;
        _warmup = warmup;
        _navigation = navigation;
        _logger = logger;
        _likes.Changed += OnLikeChanged;
    }

    /// <summary>An action ran (play, play next, add to playlist, search): the flyout should close.</summary>
    public event EventHandler? CloseRequested;

    public ObservableCollection<RadioMatchItemViewModel> Matches { get; } = [];

    /// <summary>The song as the station announced it.</summary>
    [ObservableProperty]
    public partial string HeardTitle { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string HeardArtist { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsFound), nameof(IsNoMatch), nameof(IsFailed))]
    public partial RadioMatchState State { get; set; }

    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>What was searched for: also what "Search YouTube Music" opens.</summary>
    public string Query { get; private set; } = string.Empty;

    public bool IsLoading => State == RadioMatchState.Loading;

    public bool IsFound => State == RadioMatchState.Found;

    public bool IsNoMatch => State == RadioMatchState.NoMatch;

    public bool IsFailed => State == RadioMatchState.Failed;

    public bool HasHeardArtist => !string.IsNullOrWhiteSpace(HeardArtist);

    /// <summary>Starts looking for <paramref name="song"/>.</summary>
    public void Load(RadioNowPlaying song, string? stationName)
    {
        ArgumentNullException.ThrowIfNull(song);
        _song = song;
        _stationName = stationName;
        HeardTitle = song.Title;
        HeardArtist = song.Artist ?? string.Empty;
        OnPropertyChanged(nameof(HasHeardArtist));
        Query = _matcher.GetQuery(song, stationName);
        _ = SearchAsync();
    }

    /// <summary>The flyout closed: stops the search and the like updates.</summary>
    public void Close()
    {
        _cts?.Cancel();
        _likes.Changed -= OnLikeChanged;
    }

    [RelayCommand]
    private Task RetryAsync() => SearchAsync();

    /// <summary>Opens the Search page with the same text, for more than the top three.</summary>
    [RelayCommand]
    private void SearchOnYouTubeMusic()
    {
        if (Query.Length == 0)
        {
            return;
        }

        CloseRequested?.Invoke(this, EventArgs.Empty);
        _navigation.NavigateTo(PageKey.Search, Query);
    }

    private async Task SearchAsync()
    {
        if (_song is not { } song)
        {
            return;
        }

        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Matches.Clear();
        State = RadioMatchState.Loading;
        try
        {
            var tracks = await _matcher.FindAsync(song, _stationName, 3, cts.Token);
            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var track in tracks)
            {
                Matches.Add(new RadioMatchItemViewModel(track, this, _actions));
            }

            State = Matches.Count > 0 ? RadioMatchState.Found : RadioMatchState.NoMatch;

            // The likely pick starts at once if the user plays it.
            _warmup.Warm(tracks.FirstOrDefault());
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not find \"{Query}\" on YouTube Music", Query);
            ErrorMessage = ex is HttpRequestException or TaskCanceledException
                ? "Check your connection and try again."
                : ex is HushException ? ex.Message : "Something went wrong. Try again in a moment.";
            State = RadioMatchState.Failed;
        }
    }

    internal void RequestClose() => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void OnLikeChanged(object? sender, TrackRatedEventArgs e)
    {
        foreach (var match in Matches)
        {
            match.Refresh(e.VideoId);
        }
    }
}

/// <summary>One YouTube Music song in the "On YouTube Music" flyout.</summary>
public sealed partial class RadioMatchItemViewModel : ObservableObject
{
    private readonly RadioMatchViewModel _owner;
    private readonly IMediaItemActions _actions;

    public RadioMatchItemViewModel(Track track, RadioMatchViewModel owner, IMediaItemActions actions)
    {
        Track = track;
        _owner = owner;
        _actions = actions;
        CanRate = actions.IsSignedIn;
        IsLiked = actions.GetLikeStatus(track) == LikeStatus.Like;
        Subtitle = string.Join(" · ", new[] { track.ArtistsText, track.Type == TrackType.Video ? "Video" : null }.Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    public Track Track { get; }

    public string Title => Track.Title;

    public string Subtitle { get; }

    public string DurationText => Format.Duration(Track.Duration);

    public string? ArtUrl => Track.ThumbnailFor(60)?.Url;

    /// <summary>Signed in: like and add to playlist are offered.</summary>
    public bool CanRate { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeGlyph), nameof(LikeLabel))]
    public partial bool IsLiked { get; set; }

    public string LikeGlyph => IsLiked ? "" : "";

    public string LikeLabel => IsLiked ? "Remove from liked songs" : "Like";

    internal void Refresh(string videoId)
    {
        if (videoId == Track.VideoId)
        {
            IsLiked = _actions.GetLikeStatus(Track) == LikeStatus.Like;
        }
    }

    /// <summary>Switches from the station to this song, with its Up next.</summary>
    [RelayCommand]
    private Task PlayAsync()
    {
        _owner.RequestClose();
        return _actions.PlayTrackAsync(Track);
    }

    [RelayCommand]
    private void PlayNext()
    {
        _owner.RequestClose();
        _actions.PlayNext([Track]);
    }

    [RelayCommand]
    private Task AddToPlaylistAsync()
    {
        _owner.RequestClose();
        return _actions.AddToPlaylistAsync([Track]);
    }

    [RelayCommand]
    private async Task ToggleLikeAsync()
    {
        var like = !IsLiked;
        IsLiked = like;

        // The action reports its own errors; after a failure the like state still holds the old value.
        await _actions.RateAsync(Track, like ? LikeStatus.Like : LikeStatus.Indifferent);
        IsLiked = _actions.GetLikeStatus(Track) == LikeStatus.Like;
    }
}
