using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class PlaylistViewModel : PageViewModelBase, ITrackListHost
{
    private const string LikedMusicId = "LM";

    private readonly IBrowseApi _browse;
    private readonly IAccountActionsService _account;
    private readonly IPlaylistDialogService _dialogs;
    private string? _playlistId;
    private int? _trackCount;
    private string? _durationText;
    private string? _year;

    public PlaylistViewModel(IBrowseApi browse, IAccountActionsService account, IPlaylistDialogService dialogs, PageServices services)
        : base(services)
    {
        _browse = browse;
        _account = account;
        _dialogs = dialogs;
        Tracks = TrackItem.CreateList(
            (continuation, ct) => _browse.GetPlaylistTracksAsync(continuation, ct),
            ex => ReportError("Couldn't load more songs", ex),
            () => NavigationToken);
    }

    public IncrementalCollection<TrackItem> Tracks { get; }

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
    public partial bool IsOwned { get; set; }

    public Task PlayFromTrackAsync(Track track) => PlayFromIndexAsync(IndexOf(track));

    public bool CanRemoveFromPlaylist(Track track) => IsOwned && track.SetVideoId is not null;

    public async Task RemoveFromPlaylistAsync(Track track)
    {
        if (Playlist is not { } playlist || !CanRemoveFromPlaylist(track))
        {
            return;
        }

        try
        {
            await _account.RemoveFromPlaylistAsync(playlist.PlaylistId, [track]);
            var index = IndexOf(track);
            if (index >= 0)
            {
                Tracks.RemoveAt(index);
            }

            if (_trackCount is { } count)
            {
                _trackCount = Math.Max(0, count - 1);
            }

            UpdateStats();
            UpdateKicker();
            IsEmpty = Tracks.Count == 0 && !Tracks.HasMoreItems;
            Notifications.Show(new AppNotification(NotificationSeverity.Success, "Removed from playlist", track.Title));
        }
        catch (Exception ex)
        {
            Notifications.ShowError("Couldn't remove the song", ex);
        }
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

    protected override void OnNavigatedFromCore() => _account.PlaylistChanged -= OnPlaylistChanged;

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

                Tracks.Reset(TrackItem.From(page.Tracks.Items), page.Tracks.Continuation);
                IsEmpty = Tracks.Count == 0 && !Tracks.HasMoreItems;
                Services.Warmup.Warm(page.Tracks.Items.FirstOrDefault(t => t.IsAvailable));
                HasContent = true;
            },
            "Couldn't load this playlist");
    }

    [RelayCommand]
    private Task PlayAsync() => Playlist is { } playlist ? Actions.PlayPlaylistAsync(playlist.PlaylistId) : Task.CompletedTask;

    [RelayCommand]
    private Task ShuffleAsync() => Playlist is { } playlist ? Actions.PlayPlaylistAsync(playlist.PlaylistId, shuffle: true) : Task.CompletedTask;

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayFromIndexAsync(Tracks.IndexOf(item));

    /// <summary>"Play next" / "Add to queue" from the header menu use the songs loaded so far.</summary>
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

    private Task PlayFromIndexAsync(int index)
    {
        if (index < 0 || Playlist is not { } playlist)
        {
            return Task.CompletedTask;
        }

        var source = playlist.PlaylistId == LikedMusicId
            ? new QueueSource(QueueSourceKind.LikedSongs, playlist.PlaylistId, playlist.Title)
            : new QueueSource(QueueSourceKind.Playlist, playlist.PlaylistId, playlist.Title);
        return Actions.PlayTracksAsync([.. Tracks.Select(t => t.Track)], index, source);
    }

    private void UpdateKicker() => Kicker = ItemFormat.Join("Playlist", ItemFormat.TrackCount(_trackCount), ItemFormat.PrivacyText(Privacy));

    private void UpdateStats() => Stats = ItemFormat.Join(ItemFormat.TrackCount(_trackCount), _durationText, _year);

    private List<Track> AvailableTracks() => [.. Tracks.Select(t => t.Track).Where(t => t.IsAvailable)];

    private int IndexOf(Track track)
    {
        for (var i = 0; i < Tracks.Count; i++)
        {
            if (ReferenceEquals(Tracks[i].Track, track))
            {
                return i;
            }
        }

        return -1;
    }

    // Songs added from elsewhere (track menus, player bar) while this playlist is open: reload to show them.
    private void OnPlaylistChanged(object? sender, PlaylistChangedEventArgs e)
    {
        if (e.Kind == PlaylistChangeKind.ItemsAdded && Playlist is { } playlist && e.PlaylistId == playlist.PlaylistId)
        {
            Services.Dispatcher.Run(() => _ = LoadAsync());
        }
    }
}
