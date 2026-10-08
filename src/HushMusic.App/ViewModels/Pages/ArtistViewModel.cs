using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Pages;

public sealed partial class ArtistViewModel(IBrowseApi browse, PageServices services) : PageViewModelBase(services)
{
    private string? _channelId;

    public ObservableCollection<TrackItem> TopSongs { get; } = [];

    public ObservableCollection<Shelf> Sections { get; } = [];

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
        return LoadAsync();
    }

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
                    Sections.Add(section);
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
}
