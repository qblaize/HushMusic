using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>
/// Mirrors <see cref="IQueueService.Items"/> for the "Up next" tab. Every change goes through the queue service;
/// the collection is re-synced from its snapshot (diffed by <see cref="QueueItem.Id"/> so the list doesn't flicker).
/// Songs autoplay added (<see cref="IQueueAutoplay"/>) get a "Similar songs" header above the first of them; signed in,
/// the queue can be saved as a playlist.
/// </summary>
public sealed partial class QueuePanelViewModel : ObservableObject
{
    private readonly IQueueService _queue;
    private readonly IPlayer _player;
    private readonly IMediaItemActions _actions;
    private readonly INotificationService _notifications;
    private readonly IUiDispatcher _dispatcher;
    private readonly IQueueAutoplay _autoplay;
    private readonly IAuthService _auth;
    private readonly IAccountActionsService _accountActions;
    private QueueItemViewModel? _dragged;
    private int _dragFrom = -1;
    private bool _syncPending;
    private bool _isPlaying;

    public QueuePanelViewModel(
        IQueueService queue,
        IPlayer player,
        IMediaItemActions actions,
        INotificationService notifications,
        IUiDispatcher dispatcher,
        IQueueAutoplay autoplay,
        IAuthService auth,
        IAccountActionsService accountActions)
    {
        _queue = queue;
        _player = player;
        _actions = actions;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _autoplay = autoplay;
        _auth = auth;
        _accountActions = accountActions;
        _queue.Changed += (_, _) => _dispatcher.Run(Sync);
        _queue.CurrentChanged += (_, _) => _dispatcher.Run(Sync);
        _autoplay.Changed += (_, _) => _dispatcher.Run(Sync);
        _auth.StatusChanged += (_, _) => _dispatcher.Run(() => OnPropertyChanged(nameof(CanSaveAsPlaylist)));
        _player.StatusChanged += (_, e) => _dispatcher.Run(() => SetPlaying(IsActive(e.Status)));
        _isPlaying = IsActive(player.Status);
        _dispatcher.Run(Sync);
    }

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    /// <summary>Signed in, with YouTube songs in the queue (stations can't go in a playlist).</summary>
    public bool CanSaveAsPlaylist => _auth.Status == AuthStatus.SignedIn && SavableCount > 0;

    /// <summary>The distinct YouTube songs in the queue, played ones included: what "Save as playlist" saves.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSaveAsPlaylist))]
    public partial int SavableCount { get; set; }

    /// <summary>A name to start from: where the queue came from.</summary>
    public string DefaultPlaylistName => _queue.Source switch
    {
        { Kind: QueueSourceKind.Radio, Title.Length: > 0 } source => $"{source.Title} radio",
        { Kind: QueueSourceKind.UpNext, Title.Length: > 0 } source => $"{source.Title} mix",
        { Kind: not QueueSourceKind.LiveRadio, Title.Length: > 0 } source => source.Title,
        _ => "My queue",
    };

    /// <summary>Creates a private playlist with the queue's songs, in queue order, and reports the result.</summary>
    public async Task SaveAsPlaylistAsync(string title)
    {
        var name = title.Trim();
        List<string> videoIds = [.. SavableVideoIds()];
        if (name.Length == 0 || videoIds.Count == 0)
        {
            return;
        }

        try
        {
            await _accountActions.CreatePlaylistAsync(name, null, PrivacyStatus.Private, videoIds);
            var songs = videoIds.Count == 1 ? "1 song" : $"{videoIds.Count} songs";
            _notifications.Show(new AppNotification(NotificationSeverity.Success, "Queue saved as a playlist", $"“{name}”, {songs}, private"));
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't save the queue as a playlist", ex);
        }
    }

    private IEnumerable<string> SavableVideoIds() =>
        _queue.Items.Select(i => i.Track).Where(t => !t.IsLiveRadio && t.IsAvailable).Select(t => t.VideoId).Distinct(StringComparer.Ordinal);

    [ObservableProperty]
    public partial QueueItemViewModel? Current { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(CountText))]
    public partial int Count { get; set; }

    // Stations and songs can share a queue (a song found on a station's "On YouTube Music", played next).
    public string CountText
    {
        get
        {
            var stations = Items.Count(i => i.IsStation);
            var songs = Count - stations;
            var stationText = stations == 1 ? "1 station" : $"{stations} stations";
            var songText = songs == 1 ? "1 song" : $"{songs} songs";
            return stations == 0 ? songText : songs == 0 ? stationText : $"{stationText}, {songText}";
        }
    }

    [ObservableProperty]
    public partial string? SourceText { get; set; }

    /// <summary>Songs after the current one (what "Clear" removes).</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ClearQueueCommand))]
    public partial int UpcomingCount { get; set; }

    public bool IsEmpty => Count == 0;

    /// <summary>Tracks dropped on the queue list: inserted at <paramref name="index"/> (a position in <see cref="Items"/>).</summary>
    public void InsertTracks(IReadOnlyList<Track> tracks, int index)
    {
        List<Track> playable = [.. tracks.Where(t => t.IsAvailable)];
        if (playable.Count == 0)
        {
            return;
        }

        try
        {
            var end = _queue.Items.Count;
            _queue.Enqueue(playable);
            var target = Math.Clamp(index, 0, end);
            for (var i = 0; target < end && i < playable.Count; i++)
            {
                _queue.Move(end + i, target + i);
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Couldn't change the queue", ex);
        }
    }

    /// <summary>Tracks dropped on the player bar: appended, with an "Added to queue" toast.</summary>
    public void AddToQueue(IReadOnlyList<Track> tracks) => _actions.AddToQueue(tracks);

    /// <summary>Drag-to-reorder started in the list (view glue).</summary>
    public void BeginDrag(QueueItemViewModel? item)
    {
        _dragged = item;
        _dragFrom = item is null ? -1 : Items.IndexOf(item);
    }

    /// <summary>Drag-to-reorder finished; the ListView has already moved the item inside <see cref="Items"/>.</summary>
    public void CompleteDrag(bool moved)
    {
        var item = _dragged;
        var from = _dragFrom;
        _dragged = null;
        _dragFrom = -1;

        if (moved && item is not null && from >= 0)
        {
            var to = Items.IndexOf(item);
            if (to >= 0 && to != from)
            {
                try
                {
                    _queue.Move(from, to);
                }
                catch (Exception ex)
                {
                    _notifications.ShowError("Could not reorder the queue", ex);
                    _syncPending = true;
                }
            }
        }

        if (_syncPending || moved)
        {
            Sync();
        }
    }

    /// <summary>Clears what is still to come; the current song keeps playing.</summary>
    [RelayCommand(CanExecute = nameof(CanClearQueue))]
    private void ClearQueue()
    {
        try
        {
            for (var last = _queue.Items.Count - 1; last > _queue.CurrentIndex; last--)
            {
                _queue.RemoveAt(last);
            }
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not clear the queue", ex);
        }
    }

    private bool CanClearQueue() => UpcomingCount > 0;

    private string SuggestionsNote() =>
        _autoplay.Seed is { } seed ? $"Autoplay · based on “{seed.Title}”" : "Autoplay";

    private static bool IsActive(PlaybackStatus status) =>
        status is PlaybackStatus.Playing or PlaybackStatus.Loading or PlaybackStatus.Buffering;

    private void SetPlaying(bool playing)
    {
        _isPlaying = playing;
        if (Current is { } current)
        {
            current.IsPlaying = playing;
        }
    }

    private async Task PlayAsync(QueueItemViewModel item)
    {
        var index = IndexInQueue(item);
        if (index < 0)
        {
            return;
        }

        try
        {
            await _player.PlayQueueIndexAsync(index);
        }
        catch (Exception ex)
        {
            _notifications.ShowError($"Could not play {item.Title}", ex);
        }
    }

    private void Remove(QueueItemViewModel item)
    {
        var index = IndexInQueue(item);
        if (index < 0)
        {
            return;
        }

        try
        {
            _queue.RemoveAt(index);
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not remove the song from the queue", ex);
        }
    }

    private int IndexInQueue(QueueItemViewModel item)
    {
        var items = _queue.Items;
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == item.Id)
            {
                return i;
            }
        }

        return -1;
    }

    private void Sync()
    {
        if (_dragged is not null)
        {
            // Don't reshuffle the list under the user's drag; catch up when it ends.
            _syncPending = true;
            return;
        }

        _syncPending = false;
        IReadOnlyList<QueueItem> items;
        int currentIndex;
        try
        {
            items = _queue.Items;
            currentIndex = _queue.CurrentIndex;
            SourceText = _queue.Source?.Title is { Length: > 0 } title ? $"Playing from {title}" : null;
        }
        catch (Exception ex)
        {
            _notifications.ShowError("Could not read the queue", ex);
            return;
        }

        if (items.Count == 0)
        {
            Items.Clear();
        }
        else
        {
            var existing = new Dictionary<Guid, QueueItemViewModel>(Items.Count);
            foreach (var vm in Items)
            {
                existing.TryAdd(vm.Id, vm);
            }

            for (var i = 0; i < items.Count; i++)
            {
                var queueItem = items[i];
                if (i < Items.Count && Items[i].Id == queueItem.Id)
                {
                    continue;
                }

                if (existing.TryGetValue(queueItem.Id, out var vm))
                {
                    // Positions before i already match, so an existing item can only be further down.
                    Items.Move(Items.IndexOf(vm), i);
                }
                else
                {
                    Items.Insert(i, new QueueItemViewModel(queueItem, PlayAsync, Remove));
                }
            }

            while (Items.Count > items.Count)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }

        QueueItemViewModel? current = null;
        var suggested = _autoplay.SuggestedItemIds;
        var suggestionsStart = false;
        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            var isCurrent = i == currentIndex;
            item.IsPlayed = currentIndex >= 0 && i < currentIndex;

            // IsCurrent first: the equaliser only starts when it is already visible.
            item.IsCurrent = isCurrent;
            item.IsPlaying = isCurrent && _isPlaying;
            if (isCurrent)
            {
                current = item;
            }

            var isStart = !suggestionsStart && suggested.Contains(item.Id);
            suggestionsStart |= isStart;
            item.SuggestionsNote = isStart ? SuggestionsNote() : null;
            item.IsSuggestionsStart = isStart;
        }

        SavableCount = Items.Where(i => !i.IsStation).Select(i => i.Track.VideoId).Distinct(StringComparer.Ordinal).Count();
        Count = Items.Count;
        OnPropertyChanged(nameof(CountText));
        UpcomingCount = currentIndex >= 0 ? Items.Count - currentIndex - 1 : Items.Count;
        Current = current;
    }
}

public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(QueueItem item, Func<QueueItemViewModel, Task> play, Action<QueueItemViewModel> remove)
    {
        Id = item.Id;
        var track = item.Track;
        Track = track;
        Title = track.Title;
        ArtistsText = track.ArtistsText;
        DurationText = Format.Duration(track.Duration);
        IsStation = track.IsLiveRadio;
        CoverUrl = IsStation ? null : track.ThumbnailFor(60)?.Url;
        PlayCommand = new AsyncRelayCommand(() => play(this));
        RemoveCommand = new RelayCommand(() => remove(this));
    }

    public Guid Id { get; }

    public string Title { get; }

    public string ArtistsText { get; }

    public string DurationText { get; }

    public string? CoverUrl { get; }

    /// <summary>A live radio station: its art is drawn from the logo (or initials).</summary>
    public bool IsStation { get; }

    public string? StationLogoUrl => Track.Station?.LogoUrl;

    public string? StationName => Track.Station?.Name;

    public IAsyncRelayCommand PlayCommand { get; }

    public IRelayCommand RemoveCommand { get; }

    [ObservableProperty]
    public partial bool IsCurrent { get; set; }

    /// <summary>Before the current song (history): drawn dimmed.</summary>
    [ObservableProperty]
    public partial bool IsPlayed { get; set; }

    /// <summary>Current and playing: the equaliser bounces.</summary>
    [ObservableProperty]
    public partial bool IsPlaying { get; set; }

    /// <summary>The first song autoplay added after the end of the queue: a "Similar songs" header goes above it.</summary>
    [ObservableProperty]
    public partial bool IsSuggestionsStart { get; set; }

    /// <summary>The header's second line ("Autoplay · based on …"), set with <see cref="IsSuggestionsStart"/>.</summary>
    [ObservableProperty]
    public partial string? SuggestionsNote { get; set; }

    /// <summary>The track, for dragging a queue row elsewhere.</summary>
    public Track Track { get; }
}
