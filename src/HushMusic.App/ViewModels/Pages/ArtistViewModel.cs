using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class ArtistViewModel(IBrowseApi browse, PinToggle pin, IBlocklist blocklist, PageServices services) : PageViewModelBase(services)
{
    private string? _channelId;
    private string? _cardArt;
    private ArtistRef? _blockTarget;

    /// <summary>The artist's songs stay out of radio, autoplay and the weekly mix (<see cref="IBlocklist"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BlockLabel))]
    public partial bool IsBlocked { get; private set; }

    /// <summary>The menu's "Don't play this artist" / "Unblock this artist".</summary>
    public string BlockLabel => IsBlocked ? "Unblock this artist" : "Don't play this artist";

    /// <summary>The menu's "Pin to sidebar" / "Unpin from sidebar".</summary>
    public PinToggle Pin { get; } = pin;

    public ObservableCollection<TrackItem> TopSongs { get; } = [];

    /// <summary>Albums, singles, videos... in page order, with "See all" where the artist page links a full list.</summary>
    public ObservableCollection<SeeAllShelf> Sections { get; } = [];

    [ObservableProperty]
    public partial Artist? Artist { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    /// <summary>"1.2M subscribers • 3B views".</summary>
    [ObservableProperty]
    public partial string Stats { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? Description { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasShuffle))]
    [NotifyCanExecuteChangedFor(nameof(ShuffleCommand))]
    public partial string? ShufflePlaylistId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRadio))]
    [NotifyCanExecuteChangedFor(nameof(RadioCommand))]
    public partial string? RadioPlaylistId { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAllSongs))]
    [NotifyPropertyChangedFor(nameof(SeeAllSongsText))]
    [NotifyCanExecuteChangedFor(nameof(SeeAllSongsCommand))]
    public partial string? AllSongsPlaylistId { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PlayCommand))]
    public partial bool HasTopSongs { get; set; }

    public bool HasShuffle => ShufflePlaylistId is not null;

    public bool HasRadio => RadioPlaylistId is not null;

    public bool HasAllSongs => AllSongsPlaylistId is not null;

    /// <summary>"See all" next to the top songs, when the full list exists.</summary>
    public string? SeeAllSongsText => HasAllSongs ? "See all" : null;

    protected override Task OnNavigatedToCoreAsync(object? parameter)
    {
        _channelId = parameter as string;

        // The page itself only has a wide banner: a pin made here uses the square photo of the card it was opened from.
        _cardArt = SidebarPinItems.SquareArt(Services.Previews.Take<Artist>(_channelId));
        blocklist.Changed -= OnBlocklistChanged;
        blocklist.Changed += OnBlocklistChanged;
        return LoadAsync();
    }

    protected override void OnNavigatedFromCore()
    {
        Pin.Detach();
        blocklist.Changed -= OnBlocklistChanged;
    }

    [RelayCommand]
    private Task ToggleBlockAsync() =>
        _blockTarget is { } artist ? Actions.SetArtistBlockedAsync(artist, !blocklist.IsBlocked(artist)) : Task.CompletedTask;

    // Also blocked or unblocked from a song's menu, or from Settings.
    private void OnBlocklistChanged(object? sender, EventArgs e) => Services.Dispatcher.Run(SyncBlocked);

    private void SyncBlocked() => IsBlocked = _blockTarget is { } artist && blocklist.IsBlocked(artist);

    protected override Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(_channelId))
        {
            ErrorMessage = "No artist was selected.";
            return Task.CompletedTask;
        }

        var channelId = _channelId;
        return RunAsync(
            async ct =>
            {
                var page = await browse.GetArtistAsync(channelId, ct);
                Artist = page.Artist;
                Name = page.Artist.Title;
                Stats = ItemFormat.Join(ItemFormat.SubscribersText(page.Artist.Subscribers), page.Views);
                Description = page.Description;
                ShufflePlaylistId = page.ShufflePlaylistId;
                RadioPlaylistId = page.RadioPlaylistId;
                AllSongsPlaylistId = page.AllSongsPlaylistId;
                var squareArt = _cardArt ?? SidebarPinItems.SquareArt(page.Artist);
                Pin.Attach(SidebarPinItems.ForArtist(page.Artist with { BrowseId = channelId }, squareArt), refreshArt: squareArt is not null);
                _blockTarget = new ArtistRef(page.Artist.Title, string.IsNullOrWhiteSpace(page.Artist.BrowseId) ? channelId : page.Artist.BrowseId);
                SyncBlocked();

                TopSongs.Clear();
                foreach (var item in TrackItem.From(page.TopSongs))
                {
                    TopSongs.Add(item);
                }

                HasTopSongs = TopSongs.Count > 0;
                Services.Warmup.Warm(page.TopSongs.FirstOrDefault(t => t.IsAvailable));

                Sections.Clear();
                foreach (var section in page.Sections.Where(s => s.Items.Count > 0))
                {
                    Sections.Add(new SeeAllShelf(section, SeeAllCommandFor(section, page.Artist.Title)));
                }

                HasContent = true;
            },
            "Couldn't load this artist");
    }

    /// <summary>Plays the top songs in order, as the artist's queue.</summary>
    [RelayCommand(CanExecute = nameof(HasTopSongs))]
    private Task PlayAsync() =>
        Actions.PlayTracksAsync([.. TopSongs.Select(t => t.Track)], 0, new QueueSource(QueueSourceKind.Artist, _channelId, Name));

    [RelayCommand(CanExecute = nameof(HasShuffle))]
    private Task ShuffleAsync() => Actions.PlayPlaylistAsync(ShufflePlaylistId!, shuffle: true);

    [RelayCommand(CanExecute = nameof(HasRadio))]
    private Task RadioAsync() => Actions.PlayPlaylistAsync(RadioPlaylistId!);

    [RelayCommand(CanExecute = nameof(HasAllSongs))]
    private void SeeAllSongs() => Actions.OpenPlaylist(AllSongsPlaylistId);

    [RelayCommand]
    private Task PlayTrackAsync(TrackItem? item) => item is null ? Task.CompletedTask : Actions.PlayTrackAsync(item.Track);

    // Albums / singles link the discography grid ("MPAD" + channel id, with params); videos link a playlist. Other links
    // (a channel's "Live performances", "Playlists by ...") lead to pages that are empty without an account: no "See all".
    private RelayCommand? SeeAllCommandFor(Shelf section, string artistName) => section.MoreBrowseId switch
    {
        { } id when id.StartsWith("MPAD", StringComparison.Ordinal) => new RelayCommand(() =>
            Services.Navigation.NavigateTo(PageKey.ArtistDiscography, new ArtistDiscographyRequest(id, section.MoreParams, section.Title, artistName))),
        { } id when id.StartsWith("VL", StringComparison.Ordinal) => new RelayCommand(() => Actions.OpenPlaylist(id[2..])),
        _ => null,
    };
}
