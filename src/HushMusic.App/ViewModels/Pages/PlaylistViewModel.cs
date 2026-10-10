using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class PlaylistViewModel : PageViewModelBase, ITrackListHost, ITrackSelectionHost
{
    private const string LikedMusicId = "LM";

    private static readonly PropertyChangedEventArgs ShowProgressArgs = new(nameof(ShowProgress));

    private readonly IBrowseApi _browse;
    private readonly IAccountActionsService _account;
    private readonly IPlaylistDialogService _dialogs;
    private readonly IQueueService _queue;

    // Moves go to YouTube Music one at a time, in the order they were made.
    private readonly SemaphoreSlim _moveGate = new(1, 1);
    private string? _playlistId;
    private int? _trackCount;
    private string? _durationText;
    private string? _year;
    private int _listVersion;
    private int _pendingMoves;
    private int _ownEdits;
    private TrackItem? _dragItem;
    private int _dragFrom = -1;

    public PlaylistViewModel(
        IBrowseApi browse,
        IAccountActionsService account,
        IPlaylistDialogService dialogs,
        IQueueService queue,
        PageServices services)
        : base(services)
    {
        _browse = browse;
        _account = account;
        _dialogs = dialogs;
        _queue = queue;
        Tracks = TrackItem.CreateList(
            (continuation, ct) => _browse.GetPlaylistTracksAsync(continuation, ct),
            ex => ReportError("Couldn't load more songs", ex),
            () => NavigationToken);
        Filter = new TrackListFilter(Tracks, () => NavigationToken, TrackListOwnOrder.Playlist);
        Selection = new TrackSelection(Filter.Rows, services.Actions, CurrentSource, RemoveTracksAsync, () => IsOwned);
        Filter.Arranged += (_, _) => Selection.Forget(Tracks.Except(Filter.Rows));
        Filter.PropertyChanged += OnFilterPropertyChanged;
    }

    /// <summary>The whole playlist in its own order (what moves, removals and Undo work on).</summary>
    public IncrementalCollection<TrackItem> Tracks { get; }

    /// <summary>The filter and sort above the list; its <see cref="TrackListFilter.Rows"/> are what the list shows (and selects from).</summary>
    public TrackListFilter Filter { get; }

    public TrackSelection Selection { get; }

    /// <summary>The thin progress bar: a reload, or the rest of a long playlist loading for the filter or sort.</summary>
    public bool ShowProgress => ShowBusyBar || Filter.IsLoadingRest;

    [ObservableProperty]
    public partial Playlist? Playlist { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ArtUrl { get; set; }

    /// <summary>The card's art (already cached) while the large art loads, when the playlist was opened from a card.</summary>
    [ObservableProperty]
    public partial string? PreviewArtUrl { get; set; }

    /// <summary>"Playlist · 52 songs · Private" (shown as the uppercase eyebrow above the title).</summary>
    [ObservableProperty]
    public partial string Kicker { get; set; } = string.Empty;

    /// <summary>"52 songs · 3 hours · 2024" (shown under the track list).</summary>
    [ObservableProperty]
    public partial string Stats { get; set; } = string.Empty;

    /// <summary>Account-only actions ("Add to playlist…") are offered when signed in.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReorder))]
    public partial bool IsSignedIn { get; set; }

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ArtistRef> Authors { get; set; } = [];

    [ObservableProperty]
    public partial PrivacyStatus? Privacy { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EditCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteCommand))]
    [NotifyPropertyChangedFor(nameof(CanReorder))]
    public partial bool IsOwned { get; set; }

    /// <summary>Songs can be dragged into a new order (your own playlists, shown unfiltered in their own order).</summary>
    public bool CanReorder => IsReorderable && !Filter.IsActive;

    private bool IsReorderable => IsOwned && IsSignedIn && _playlistId is not (LikedMusicId or "VL" + LikedMusicId);

    public Task PlayFromTrackAsync(Track track) => PlayRowsAsync(IndexOf(Filter.Rows, track));

    public bool CanRemoveFromPlaylist(Track track) => IsOwned && track.SetVideoId is not null;

    public Task RemoveFromPlaylistAsync(Track track)
    {
        var index = IndexOf(Tracks, track);
        return index < 0 ? Task.CompletedTask : RemoveTracksAsync([Tracks[index]]);
    }

    /// <summary>The list started dragging a row (to reorder it, or out to the queue or a playlist).</summary>
    public void BeginReorder(object? item)
    {
        _dragItem = item as TrackItem;
        _dragFrom = _dragItem is null ? -1 : Tracks.IndexOf(_dragItem);
    }

    /// <summary>
    /// The drag ended. When the list moved the row (<paramref name="moved"/>), the new order is saved to the playlist; if
    /// that fails the row goes back.
    /// </summary>
    public void CompleteReorder(bool moved)
    {
        var item = _dragItem;
        var from = _dragFrom;
        _dragItem = null;
        _dragFrom = -1;
        if (!moved || item is null || from < 0 || Filter.IsActive || Playlist is not { } playlist)
        {
            return;
        }

        var to = Tracks.IndexOf(item);
        if (to < 0 || to == from)
        {
            return;
        }

        var successor = to + 1 < Tracks.Count ? Tracks[to + 1] : null;
        if (!CanReorder || item.Track.SetVideoId is null || successor is { Track.SetVideoId: null })
        {
            MoveRow(to, from);
            return;
        }

        if (successor is null && Tracks.HasMoreItems)
        {
            // The last loaded song isn't the end of the playlist, and the entry after it isn't known yet.
            MoveRow(to, from);
            Notifications.ShowInfo("Can't move the song there yet", "Scroll down until the rest of the playlist has loaded, then move it again.");
            return;
        }

        _ = SaveMoveAsync(playlist.PlaylistId, item, from, to, successor);
    }

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        _playlistId = parameter as string;
        _account.PlaylistChanged += OnPlaylistChanged;
        if (Services.Previews.Take<Playlist>(_playlistId) is { } preview)
        {
            // Opened from a card or suggestion: the header shows what is known while the page loads.
            Title = preview.Title;
            Kicker = "Playlist";
            Authors = preview.Author is { } author ? [author] : [];
            PreviewArtUrl = ItemFormat.CardArt(preview);
            ArtUrl = ItemFormat.HeaderArt(preview);
        }

        return LoadAsync();
    }

    protected override void OnNavigatedFromCore()
    {
        _account.PlaylistChanged -= OnPlaylistChanged;
        Selection.Exit();
    }

    protected override Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_playlistId))
        {
            ErrorMessage = "No playlist was selected.";
            return Task.CompletedTask;
        }

        var playlistId = _playlistId;
        IsSignedIn = Actions.IsSignedIn;
        return RunAsync(
            async ct =>
            {
                var page = await _browse.GetPlaylistAsync(playlistId, ct);
                var playlist = page.Playlist;
                Playlist = playlist;
                Title = playlist.Title;
                ArtUrl ??= ItemFormat.HeaderArt(playlist);
                Description = playlist.Description;
                Authors = playlist.Author is { } author ? [author] : [];
                Privacy = page.Privacy;
                IsOwned = page.IsOwned;
                _year = page.Year;
                _trackCount = playlist.TrackCount;
                _durationText = page.DurationText;
                UpdateKicker();
                UpdateStats();

                _listVersion++;
                Tracks.Reset(TrackItem.From(page.Tracks.Items), page.Tracks.Continuation);
                IsEmpty = Tracks.Count == 0 && !Tracks.HasMoreItems;
                Services.Warmup.Warm(page.Tracks.Items.FirstOrDefault(t => t.IsAvailable));
                HasContent = true;
            },
            "Couldn't load this playlist");
    }

    /// <summary>Plays the playlist, or while it is filtered or sorted the songs shown, in that order.</summary>
    [RelayCommand]
    private async Task PlayAsync()
    {
        if (Playlist is not { } playlist)
        {
            return;
        }

        if (!Filter.IsActive)
        {
            await Actions.PlayPlaylistAsync(playlist.PlaylistId);
            return;
        }

        await Filter.WhenCompleteAsync();
        if (!NavigationToken.IsCancellationRequested)
        {
            await PlayRowsAsync(0);
        }
    }

    [RelayCommand]
    private async Task ShuffleAsync()
    {
        if (Playlist is not { } playlist)
        {
            return;
        }

        if (!Filter.IsActive)
        {
            await Actions.PlayPlaylistAsync(playlist.PlaylistId, shuffle: true);
            return;
        }

        await Filter.WhenCompleteAsync();
        if (AvailableTracks() is { Count: > 0 } tracks && !NavigationToken.IsCancellationRequested)
        {
            var source = CurrentSource();
            await Actions.PlayTracksAsync(tracks, Random.Shared.Next(tracks.Count), source);
            if (_queue.Source == source && !_queue.IsShuffled)
            {
                _queue.SetShuffle(true);
            }
        }
    }

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayRowsAsync(Filter.Rows.IndexOf(item));

    /// <summary>"Play next" / "Add to queue" from the header menu use the songs shown (loaded so far, when unfiltered).</summary>
    [RelayCommand]
    private void PlayNext()
    {
        if (AvailableTracks() is { Count: > 0 } tracks)
        {
            Actions.PlayNext(tracks);
        }
    }

    [RelayCommand]
    private void AddToQueue()
    {
        if (AvailableTracks() is { Count: > 0 } tracks)
        {
            Actions.AddToQueue(tracks);
        }
    }

    [RelayCommand]
    private Task AddToPlaylistAsync() => Actions.AddToPlaylistAsync(AvailableTracks());

    [RelayCommand(CanExecute = nameof(IsOwned))]
    private async Task EditAsync()
    {
        if (Playlist is not { } playlist)
        {
            return;
        }

        var current = new PlaylistDetails(Title, Description, Privacy ?? PrivacyStatus.Private);
        var edited = await _dialogs.PromptEditAsync(current);
        if (edited is null || edited == current)
        {
            return;
        }

        try
        {
            await _account.EditPlaylistAsync(
                playlist.PlaylistId,
                edited.Title != current.Title ? edited.Title : null,
                edited.Description != current.Description ? edited.Description ?? string.Empty : null,
                edited.Privacy != current.Privacy ? edited.Privacy : null);

            Title = edited.Title;
            Description = edited.Description;
            Privacy = edited.Privacy;
            Playlist = playlist with { Title = edited.Title, Description = edited.Description };
            UpdateKicker();
            Notifications.Show(new AppNotification(NotificationSeverity.Success, "Playlist updated", edited.Title));
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't update the playlist", ex);
        }
    }

    [RelayCommand(CanExecute = nameof(IsOwned))]
    private async Task DeleteAsync()
    {
        if (Playlist is not { } playlist || !await _dialogs.ConfirmDeleteAsync(Title))
        {
            return;
        }

        try
        {
            await _account.DeletePlaylistAsync(playlist.PlaylistId);
            Notifications.Show(new AppNotification(NotificationSeverity.Success, "Playlist deleted", Title));
            if (Services.Navigation.CanGoBack)
            {
                Services.Navigation.GoBack();
            }
            else
            {
                Services.Navigation.NavigateTo(PageKey.Playlists);
            }
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't delete the playlist", ex);
        }
    }

    // ===== Removing songs (optimistic, with Undo) =====

    private async Task RemoveTracksAsync(IReadOnlyList<TrackItem> items)
    {
        if (Playlist is not { } playlist)
        {
            return;
        }

        List<RemovedRow> rows =
        [
            .. items
                .Select(item => new RemovedRow(item, Tracks.IndexOf(item)))
                .Where(row => row.Index >= 0 && CanRemoveFromPlaylist(row.Item.Track))
                .OrderBy(row => row.Index),
        ];
        if (rows.Count == 0)
        {
            return;
        }

        // What followed each entry, so Undo can put it back in place. The entry after the last loaded song isn't known.
        List<RemovedPlaylistEntry> entries =
        [
            .. rows.Select(row => new RemovedPlaylistEntry(row.Item.Track, row.Index + 1 < Tracks.Count ? Tracks[row.Index + 1].Track.SetVideoId : null)),
        ];
        var version = _listVersion;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            Tracks.RemoveAt(rows[i].Index);
        }

        Selection.Forget(rows.Select(row => row.Item));
        if (!Selection.HasSelection)
        {
            Selection.Exit();
        }

        AdjustTrackCount(-rows.Count);

        Interlocked.Increment(ref _ownEdits);
        try
        {
            await _account.RemoveFromPlaylistAsync(playlist.PlaylistId, [.. rows.Select(row => row.Item.Track)]);
            var title = rows.Count == 1 ? "Removed from playlist" : $"Removed {rows.Count} songs";
            var message = rows.Count == 1 ? rows[0].Item.Track.Title : playlist.Title;
            Notifications.Show(new AppNotification(NotificationSeverity.Success, title, message)
            {
                Action = new NotificationAction("Undo", () => _ = UndoRemoveAsync(playlist.PlaylistId, rows, entries, version)),
            });
        }
        catch (Exception ex)
        {
            if (version == _listVersion)
            {
                PutBack(rows);
            }
            else
            {
                _ = LoadAsync(); // reloaded meanwhile: show what the playlist really has
            }

            Notifications.ShowError(rows.Count == 1 ? "Couldn't remove the song" : "Couldn't remove the songs", ex);
        }
        finally
        {
            Interlocked.Decrement(ref _ownEdits);
        }
    }

    /// <param name="version">The list the rows were removed from; their indexes only apply to it.</param>
    private async Task UndoRemoveAsync(string playlistId, List<RemovedRow> rows, List<RemovedPlaylistEntry> entries, int version)
    {
        // The notification can outlive the page: the account is updated either way, the list only while it is shown.
        var onPage = !NavigationToken.IsCancellationRequested && Playlist?.PlaylistId == playlistId;
        var inPlace = onPage && version == _listVersion;
        if (inPlace)
        {
            PutBack(rows);
        }

        Interlocked.Increment(ref _ownEdits);
        try
        {
            var restored = await _account.RestorePlaylistItemsAsync(playlistId, entries);
            if (inPlace && version == _listVersion)
            {
                // The entries came back as new ones: keep their new ids for the next move or removal.
                for (var i = 0; i < rows.Count && i < restored.Count; i++)
                {
                    var index = Tracks.IndexOf(rows[i].Item);
                    if (index >= 0)
                    {
                        Tracks[index] = new TrackItem(restored[i], rows[i].Item.Number);
                    }
                }
            }
            else if (onPage && !NavigationToken.IsCancellationRequested)
            {
                _ = LoadAsync();
            }

            Notifications.Show(new AppNotification(
                NotificationSeverity.Success,
                rows.Count == 1 ? "Song put back" : $"{rows.Count} songs put back",
                rows.Count == 1 ? rows[0].Item.Track.Title : string.Empty));
        }
        catch (Exception ex)
        {
            Notifications.ShowError(rows.Count == 1 ? "Couldn't put the song back" : "Couldn't put the songs back", ex);
            if (onPage && !NavigationToken.IsCancellationRequested)
            {
                _ = LoadAsync();
            }
        }
        finally
        {
            Interlocked.Decrement(ref _ownEdits);
        }
    }

    // Rows go back to their old places (in ascending order, so every index is right when it is used).
    private void PutBack(List<RemovedRow> rows)
    {
        foreach (var row in rows)
        {
            if (Tracks.IndexOf(row.Item) < 0)
            {
                Tracks.Insert(Math.Min(row.Index, Tracks.Count), row.Item);
            }
        }

        AdjustTrackCount(rows.Count);
    }

    // ===== Reordering =====

    private async Task SaveMoveAsync(string playlistId, TrackItem item, int from, int to, TrackItem? successor)
    {
        _pendingMoves++;
        Interlocked.Increment(ref _ownEdits);
        await _moveGate.WaitAsync();
        try
        {
            await _account.MovePlaylistItemAsync(playlistId, item.Track, successor?.Track);
        }
        catch (Exception ex)
        {
            // Put the song back; with later moves still on their way its old place may have changed, so reload instead.
            if (_pendingMoves == 1 && Tracks.IndexOf(item) == to)
            {
                MoveRow(to, from);
            }
            else if (!NavigationToken.IsCancellationRequested)
            {
                _ = LoadAsync();
            }

            Notifications.ShowError("Couldn't move the song", ex);
        }
        finally
        {
            _pendingMoves--;
            Interlocked.Decrement(ref _ownEdits);
            _moveGate.Release();
        }
    }

    // Remove + insert rather than Move: list views handle these two notifications everywhere.
    private void MoveRow(int from, int to)
    {
        var item = Tracks[from];
        Tracks.RemoveAt(from);
        Tracks.Insert(Math.Min(to, Tracks.Count), item);
    }

    // ===== Helpers =====

    private QueueSource CurrentSource() => Playlist is { } playlist
        ? new QueueSource(playlist.PlaylistId == LikedMusicId ? QueueSourceKind.LikedSongs : QueueSourceKind.Playlist, playlist.PlaylistId, playlist.Title)
        : new QueueSource(QueueSourceKind.Manual, null, Title);

    // The songs shown, from the one at index (a row of Filter.Rows).
    private Task PlayRowsAsync(int index)
    {
        if (index < 0 || Playlist is null)
        {
            return Task.CompletedTask;
        }

        return Actions.PlayTracksAsync([.. Filter.Rows.Select(t => t.Track)], index, CurrentSource());
    }

    private void AdjustTrackCount(int delta)
    {
        if (_trackCount is { } count)
        {
            _trackCount = Math.Max(0, count + delta);
        }

        UpdateStats();
        UpdateKicker();
        IsEmpty = Tracks.Count == 0 && !Tracks.HasMoreItems;
    }

    private void UpdateKicker() => Kicker = ItemFormat.Join("Playlist", ItemFormat.TrackCount(_trackCount), ItemFormat.PrivacyText(Privacy));

    private void UpdateStats() => Stats = ItemFormat.Join(ItemFormat.TrackCount(_trackCount), _durationText, _year);

    private List<Track> AvailableTracks() => [.. Filter.Rows.Select(t => t.Track).Where(t => t.IsAvailable)];

    private static int IndexOf(IList<TrackItem> rows, Track track)
    {
        for (var i = 0; i < rows.Count; i++)
        {
            if (ReferenceEquals(rows[i].Track, track))
            {
                return i;
            }
        }

        return -1;
    }

    partial void OnIsOwnedChanged(bool value) => Filter.OffersReorder = IsReorderable;

    partial void OnIsSignedInChanged(bool value) => Filter.OffersReorder = IsReorderable;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(IsBusy) or nameof(HasContent))
        {
            base.OnPropertyChanged(ShowProgressArgs);
        }
    }

    private void OnFilterPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackListFilter.IsActive))
        {
            OnPropertyChanged(nameof(CanReorder));
        }
        else if (e.PropertyName == nameof(TrackListFilter.IsLoadingRest))
        {
            OnPropertyChanged(ShowProgressArgs);
        }
    }

    // Songs added from elsewhere (track menus, player bar) while this playlist is open: reload to show them. This page's
    // own edits (Undo adds songs back) are already on screen.
    private void OnPlaylistChanged(object? sender, PlaylistChangedEventArgs e)
    {
        if (e.Kind == PlaylistChangeKind.ItemsAdded && Volatile.Read(ref _ownEdits) == 0 && Playlist is { } playlist && e.PlaylistId == playlist.PlaylistId)
        {
            Services.Dispatcher.Run(() => _ = LoadAsync());
        }
    }

    private sealed record RemovedRow(TrackItem Item, int Index);
}
