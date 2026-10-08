using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class AlbumViewModel : PageViewModelBase, ITrackListHost, ITrackSelectionHost
{
    private readonly IBrowseApi _browse;
    private string? _browseId;

    public AlbumViewModel(IBrowseApi browse, PageServices services)
        : base(services)
    {
        _browse = browse;
        Selection = new TrackSelection(Tracks, services.Actions, CurrentSource);
    }

    public ObservableCollection<TrackItem> Tracks { get; } = [];

    public TrackSelection Selection { get; }

    [ObservableProperty]
    public partial Album? Album { get; set; }

    [ObservableProperty]
    public partial string Title { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? ArtUrl { get; set; }

    /// <summary>The card's art (already cached) while the large art loads, when the album was opened from a card.</summary>
    [ObservableProperty]
    public partial string? PreviewArtUrl { get; set; }

    /// <summary>"Album · 2021" (shown as the uppercase eyebrow above the title).</summary>
    [ObservableProperty]
    public partial string Kicker { get; set; } = string.Empty;

    /// <summary>"12 songs · 48 minutes" (shown under the track list).</summary>
    [ObservableProperty]
    public partial string Stats { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ArtistRef> Artists { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOtherVersions))]
    public partial IReadOnlyList<Album> OtherVersions { get; set; } = [];

    /// <summary>Account-only actions ("Add to playlist…") are offered when signed in.</summary>
    [ObservableProperty]
    public partial bool IsSignedIn { get; set; }

    public bool HasOtherVersions => OtherVersions.Count > 0;

    public Task PlayFromTrackAsync(Track track) => PlayFromIndexAsync(IndexOf(track));

    public bool CanRemoveFromPlaylist(Track track) => false;

    public Task RemoveFromPlaylistAsync(Track track) => Task.CompletedTask;

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        _browseId = parameter as string;
        if (Services.Previews.Take<Album>(_browseId) is { } preview)
        {
            // Opened from a card or suggestion: the header shows what is known while the page loads.
            Title = preview.Title;
            Kicker = ItemFormat.Join(ItemFormat.AlbumTypeText(preview.Type) ?? "Album", preview.Year);
            Artists = preview.Artists;
            PreviewArtUrl = ItemFormat.CardArt(preview);
            ArtUrl = ItemFormat.HeaderArt(preview);
        }

        return LoadAsync();
    }

    protected override void OnNavigatedFromCore() => Selection.Exit();

    protected override Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_browseId))
        {
            ErrorMessage = "No album was selected.";
            return Task.CompletedTask;
        }

        var browseId = _browseId;
        IsSignedIn = Actions.IsSignedIn;
        return RunAsync(
            async ct =>
            {
                var page = await _browse.GetAlbumAsync(browseId, ct);
                var album = page.Album;
                Album = album;
                Title = album.Title;
                ArtUrl ??= ItemFormat.HeaderArt(album);
                Kicker = ItemFormat.Join(ItemFormat.AlbumTypeText(album.Type) ?? "Album", album.Year);
                Artists = album.Artists;
                Description = page.Description;
                Stats = ItemFormat.Join(ItemFormat.TrackCount(page.TrackCount ?? page.Tracks.Count), page.DurationText);
                OtherVersions = page.OtherVersions;

                Selection.Exit();
                Tracks.Clear();
                foreach (var item in TrackItem.From(page.Tracks))
                {
                    Tracks.Add(item);
                }

                Services.Warmup.Warm(page.Tracks.FirstOrDefault(t => t.IsAvailable));
                HasContent = true;
            },
            "Couldn't load this album");
    }

    [RelayCommand]
    private Task PlayAsync() => Album is { } album ? Actions.PlayAlbumAsync(album) : Task.CompletedTask;

    [RelayCommand]
    private Task ShuffleAsync() => Album is { } album ? Actions.PlayAlbumAsync(album, shuffle: true) : Task.CompletedTask;

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : PlayFromIndexAsync(Tracks.IndexOf(item));

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

    private List<Track> AvailableTracks() => [.. Tracks.Select(t => t.Track).Where(t => t.IsAvailable)];

    private Task PlayFromIndexAsync(int index)
    {
        if (index < 0 || CurrentSource() is not { } source)
        {
            return Task.CompletedTask;
        }

        return Actions.PlayTracksAsync([.. Tracks.Select(t => t.Track)], index, source);
    }

    private QueueSource? CurrentSource() => Album is { } album ? new QueueSource(QueueSourceKind.Album, album.BrowseId, album.Title) : null;

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
}
