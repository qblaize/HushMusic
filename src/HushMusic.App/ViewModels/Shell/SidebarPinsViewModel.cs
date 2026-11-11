using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using HushMusic.App.Services.Pages;
using HushMusic.App.ViewModels.NowPlaying;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.ViewModels.Shell;

/// <summary>
/// The pinned playlists, albums, artists and stations under the rail's main items. Follows <see cref="ISidebarPins"/>
/// and the current page (a pin is selected while its page is open). Event-driven only: nothing runs while idle.
/// </summary>
public sealed partial class SidebarPinsViewModel : ObservableObject
{
    private readonly ISidebarPins _pins;
    private readonly ISidebarPinActions _pinActions;
    private readonly IMediaItemActions _actions;
    private readonly INavigationService _navigation;
    private readonly NowPlayingViewModel _nowPlaying;
    private readonly IUiDispatcher _dispatcher;
    private readonly ILogger<SidebarPinsViewModel> _logger;

    public SidebarPinsViewModel(
        ISidebarPins pins,
        ISidebarPinActions pinActions,
        IMediaItemActions actions,
        INavigationService navigation,
        NowPlayingViewModel nowPlaying,
        IUiDispatcher dispatcher,
        ILogger<SidebarPinsViewModel> logger)
    {
        _pins = pins;
        _pinActions = pinActions;
        _actions = actions;
        _navigation = navigation;
        _nowPlaying = nowPlaying;
        _dispatcher = dispatcher;
        _logger = logger;
        _pins.Changed += (_, _) => _dispatcher.Run(Sync);
        _navigation.Navigated += (_, _) => UpdateSelection();
        Sync();
        _ = LoadAsync();
    }

    /// <summary>In sidebar order.</summary>
    public ObservableCollection<PinItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool HasPins { get; private set; }

    internal void Open(PinItem item)
    {
        var pin = item.Pin;
        switch (pin.Kind)
        {
            case PinKind.Station:
                _ = PlayStationAsync(pin);
                return;
            case PinKind.Playlist:
                OpenPage(new Playlist { PlaylistId = pin.Id, Title = pin.Title });
                return;
            case PinKind.Album:
                OpenPage(new Album { BrowseId = pin.Id, Title = pin.Title });
                return;
            case PinKind.Artist:
                OpenPage(new Artist { BrowseId = pin.Id, Title = pin.Title });
                return;
        }
    }

    internal Task UnpinAsync(PinItem item) => _pinActions.UnpinAsync(item.Pin);

    internal Task MoveAsync(PinItem item, int offset) => _pinActions.MoveAsync(item.Pin, offset);

    // Like the rail's own items: the rail stays usable under Now Playing, so it closes first. The page header shows the
    // pin's title while the page loads.
    private void OpenPage(MediaItem preview)
    {
        _nowPlaying.Close();
        _actions.Open(preview);
    }

    // Stations have no page: the pin plays it, with the other pinned stations queued so Next and Previous switch between them.
    private Task PlayStationAsync(SidebarPin pin)
    {
        var stations = Items.Select(i => i.Pin.Station).OfType<RadioStation>().ToList();
        var index = stations.FindIndex(s => s.Id == pin.Id);
        return index < 0
            ? Task.CompletedTask
            : _actions.PlayTracksAsync([.. stations.Select(s => s.ToTrack())], index, new QueueSource(QueueSourceKind.LiveRadio, "pins", "Pinned stations"));
    }

    private async Task LoadAsync()
    {
        try
        {
            await _pins.LoadAsync();
            _dispatcher.Run(Sync);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load the sidebar pins");
        }
    }

    // Keeps the items that are still pinned (their art stays loaded), in the new order.
    private void Sync()
    {
        var pins = _pins.Items;
        for (var i = Items.Count - 1; i >= 0; i--)
        {
            if (!pins.Any(p => p.Is(Items[i].Pin.Kind, Items[i].Pin.Id)))
            {
                Items.RemoveAt(i);
            }
        }

        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            var at = IndexOf(pin, i);
            if (at < 0)
            {
                Items.Insert(i, new PinItem(pin, this));
                continue;
            }

            var item = Items[at];
            if (at != i)
            {
                // Remove + insert rather than Move: every items control handles these two.
                Items.RemoveAt(at);
                Items.Insert(i, item);
            }

            if (!Equals(item.Pin, pin))
            {
                item.Pin = pin;
            }
        }

        for (var i = 0; i < Items.Count; i++)
        {
            Items[i].CanMoveUp = i > 0;
            Items[i].CanMoveDown = i < Items.Count - 1;
        }

        HasPins = Items.Count > 0;
        UpdateSelection();
    }

    private int IndexOf(SidebarPin pin, int start)
    {
        for (var i = start; i < Items.Count; i++)
        {
            if (Items[i].Pin.Is(pin.Kind, pin.Id))
            {
                return i;
            }
        }

        return -1;
    }

    private void UpdateSelection()
    {
        var page = _navigation.CurrentPage switch
        {
            PageKey.Playlist => PinKind.Playlist,
            PageKey.Album => PinKind.Album,
            PageKey.Artist => PinKind.Artist,
            _ => (PinKind?)null,
        };
        var id = _navigation.CurrentParameter as string;
        foreach (var item in Items)
        {
            item.IsSelected = page is { } kind && item.Pin.Is(kind, id);
        }
    }
}

/// <summary>One pin on the rail: its art, label, selected state and menu (Unpin, Move up, Move down).</summary>
public sealed partial class PinItem(SidebarPin pin, SidebarPinsViewModel owner) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ThumbnailUrl), nameof(StationName), nameof(StationLogoUrl))]
    public partial SidebarPin Pin { get; set; } = pin;

    public string Title => Pin.Title;

    public string? ThumbnailUrl => Pin.Kind == PinKind.Station ? null : Pin.ThumbnailUrl;

    /// <summary>Station pins show the station's logo, or generated art with its initials. Null for other pins.</summary>
    public string? StationName => Pin.Kind == PinKind.Station ? Pin.Station?.Name ?? Pin.Title : null;

    public string? StationLogoUrl => Pin.Kind == PinKind.Station ? Pin.Station?.LogoUrl : null;

    public bool IsRound => Pin.Kind == PinKind.Artist;

    /// <summary>Shown until the art has loaded (or when there is none).</summary>
    public string Glyph => Pin.Kind switch
    {
        PinKind.Artist => "",
        PinKind.Album => "",
        PinKind.Station => "",
        _ => "",
    };

    /// <summary>For screen readers, after the title: "Pinned playlist".</summary>
    public string Description => Pin.Kind switch
    {
        PinKind.Artist => "Pinned artist",
        PinKind.Album => "Pinned album",
        PinKind.Station => "Pinned station",
        _ => "Pinned playlist",
    };

    /// <summary>Its page is open.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveUpCommand))]
    public partial bool CanMoveUp { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MoveDownCommand))]
    public partial bool CanMoveDown { get; set; }

    [RelayCommand]
    private void Open() => owner.Open(this);

    [RelayCommand]
    private Task UnpinAsync() => owner.UnpinAsync(this);

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private Task MoveUpAsync() => owner.MoveAsync(this, -1);

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private Task MoveDownAsync() => owner.MoveAsync(this, 1);
}
