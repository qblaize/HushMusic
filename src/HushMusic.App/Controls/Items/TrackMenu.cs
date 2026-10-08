using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using HushMusic.App.Services.Pages;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// The track context menu (right click, Shift+F10, the menu key or a row's "more" button).
/// <list type="bullet">
/// <item><c>TrackMenu.Track</c> on any element gives that element the menu for one track.</item>
/// <item><c>TrackMenu.IsEnabled</c> on a ListView/GridView gives every track item in it the menu, including keyboard invocation on the focused item.</item>
/// <item><c>TrackMenu.Host</c> on an ancestor (usually the page root, bound to the view model) supplies the
/// page context: "Play" plays within the album/playlist and "Remove from playlist" appears on owned playlists.</item>
/// </list>
/// In a list with multi-select (<see cref="TrackSelectionList"/>) the menu offers "Select", and on a selected song while
/// several are selected it acts on the whole selection.
/// </summary>
public static class TrackMenu
{
    public static readonly DependencyProperty TrackProperty = DependencyProperty.RegisterAttached(
        "Track", typeof(object), typeof(TrackMenu), new PropertyMetadata(null, OnTrackChanged));

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrackMenu), new PropertyMetadata(false, OnIsEnabledChanged));

    public static readonly DependencyProperty HostProperty = DependencyProperty.RegisterAttached(
        "Host", typeof(object), typeof(TrackMenu), new PropertyMetadata(null));

    private static Style? s_presenterStyle;
    private static Style? s_itemStyle;
    private static Style? s_subItemStyle;

    /// <summary>Rounded, compact menu chrome shared by every page menu (PageResources.xaml).</summary>
    public static Style PresenterStyle => s_presenterStyle ??= (Style)Application.Current.Resources["AppMenuFlyoutPresenterStyle"];

    public static Style ItemStyle => s_itemStyle ??= (Style)Application.Current.Resources["AppMenuFlyoutItemStyle"];

    public static Style SubItemStyle => s_subItemStyle ??= (Style)Application.Current.Resources["AppMenuFlyoutSubItemStyle"];

    public static object? GetTrack(DependencyObject element) => element.GetValue(TrackProperty);

    public static void SetTrack(DependencyObject element, object? value) => element.SetValue(TrackProperty, value);

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    public static object? GetHost(DependencyObject element) => element.GetValue(HostProperty);

    public static void SetHost(DependencyObject element, object? value) => element.SetValue(HostProperty, value);

    /// <summary>Opens the menu under <paramref name="target"/>, e.g. from a "more" button.</summary>
    /// <remarks>Like any flyout, the menu takes the theme of the element it opens from (light window or dark Now Playing).</remarks>
    public static void ShowAt(FrameworkElement target, Track track, TrackItem? item = null) =>
        Create(track, FindHost(target), item, TrackSelectionList.Find(target))
            .ShowAt(target, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });

    private static void OnTrackChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.ContextRequested -= OnElementContextRequested;
            if (e.NewValue is not null)
            {
                element.ContextRequested += OnElementContextRequested;
            }
        }
    }

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ListViewBase list)
        {
            list.ContextRequested -= OnListContextRequested;
            if (e.NewValue is true)
            {
                list.ContextRequested += OnListContextRequested;
            }
        }
    }

    private static void OnElementContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (GetTrack(sender) is Track track && sender is FrameworkElement target)
        {
            args.Handled = true;
            Show(target, track, args);
        }
    }

    private static void OnListContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not ListViewBase list || args.OriginalSource is not DependencyObject source)
        {
            return;
        }

        var container = VisualTreeSearch.FindAncestorOrSelf<SelectorItem>(source, list);
        var item = container is null ? null : list.ItemFromContainer(container);
        var track = item switch
        {
            Track t => t,
            TrackItem trackItem => trackItem.Track,
            _ => null,
        };

        if (container is not null && track is not null)
        {
            args.Handled = true;
            Show(container, track, args, item as TrackItem);
        }
    }

    private static void Show(FrameworkElement target, Track track, ContextRequestedEventArgs args, TrackItem? item = null)
    {
        var menu = Create(track, FindHost(target), item, item is null ? null : TrackSelectionList.Find(target));
        if (args.TryGetPosition(target, out var point))
        {
            menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(target);
        }
    }

    private static ITrackListHost? FindHost(DependencyObject start)
    {
        for (var current = start; current is not null; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (GetHost(current) is ITrackListHost host)
            {
                return host;
            }
        }

        return null;
    }

    private static MenuFlyout Create(Track track, ITrackListHost? host, TrackItem? item = null, TrackSelection? selection = null)
    {
        if (track.Station is { } station)
        {
            return CreateStationMenu(station);
        }

        if (item is not null && selection is { IsActive: true, Count: > 1 } && selection.IsSelected(item))
        {
            return CreateSelectionMenu(selection);
        }

        var actions = App.GetService<IMediaItemActions>();
        IReadOnlyList<Track> single = [track];
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = PresenterStyle };

        menu.Items.Add(Item("Play", "", () => host is not null ? host.PlayFromTrackAsync(track) : actions.PlayTrackAsync(track)));
        menu.Items.Add(Item("Play next", "", () => actions.PlayNext(single)));
        menu.Items.Add(Item("Add to queue", "", () => actions.AddToQueue(single)));
        menu.Items.Add(Item("Start radio", "", () => actions.StartRadioAsync(track)));

        if (actions.IsSignedIn)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(actions.GetLikeStatus(track) == LikeStatus.Like
                ? Item("Remove like", "", () => actions.RateAsync(track, LikeStatus.Indifferent))
                : Item("Like", "", () => actions.RateAsync(track, LikeStatus.Like)));
            menu.Items.Add(Item("Add to playlist…", "", () => actions.AddToPlaylistAsync(single)));
        }

        var album = track.Album?.BrowseId;
        var artists = track.Artists.Where(a => !string.IsNullOrWhiteSpace(a.BrowseId)).ToList();
        if (album is not null || artists.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (album is not null)
        {
            menu.Items.Add(Item("Go to album", "", () => actions.OpenAlbum(album)));
        }

        if (artists.Count == 1)
        {
            menu.Items.Add(Item("Go to artist", "", () => actions.OpenArtist(artists[0].BrowseId)));
        }
        else if (artists.Count > 1)
        {
            var sub = new MenuFlyoutSubItem { Text = "Go to artist", Icon = new FontIcon { Glyph = "" }, Style = SubItemStyle };
            foreach (var artist in artists)
            {
                sub.Items.Add(Item(artist.Name, null, () => actions.OpenArtist(artist.BrowseId)));
            }

            menu.Items.Add(sub);
        }

        if (host?.CanRemoveFromPlaylist(track) == true)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Remove from playlist", "", () => host.RemoveFromPlaylistAsync(track)));
        }

        if (item is not null && selection is not null)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(selection.IsSelected(item)
                ? Item("Deselect", "", () => selection.Toggle(item))
                : Item("Select", "", () => selection.Toggle(item)));
        }

        return menu;
    }

    // Right-click on one of several selected songs: the actions of the selection bar.
    private static MenuFlyout CreateSelectionMenu(TrackSelection selection)
    {
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = PresenterStyle };
        menu.Items.Add(Item($"Play {selection.Count} songs", "", () => selection.PlayCommand.ExecuteAsync(null)));
        menu.Items.Add(Item("Play next", "", () => selection.PlayNextCommand.Execute(null)));
        menu.Items.Add(Item("Add to queue", "", () => selection.AddToQueueCommand.Execute(null)));
        if (selection.IsSignedIn)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Add to playlist…", "", () => selection.AddToPlaylistCommand.ExecuteAsync(null)));
        }

        if (selection.CanRemove)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            menu.Items.Add(Item("Remove from playlist", "", () => selection.RemoveCommand.ExecuteAsync(null)));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Clear selection", "", selection.Exit));
        return menu;
    }

    // A live radio station: nothing YouTube can do with it; favourites and its website instead.
    private static MenuFlyout CreateStationMenu(RadioStation station)
    {
        var favorites = App.GetService<IRadioFavorites>();
        var notifications = App.GetService<INotificationService>();
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = PresenterStyle };
        var isFavorite = favorites.Contains(station.Id);
        menu.Items.Add(Item(isFavorite ? "Remove from favourites" : "Add to favourites", isFavorite ? "\uEB52" : "\uEB51", async () =>
        {
            try
            {
                await favorites.SetAsync(station, !isFavorite);
            }
            catch (Exception ex)
            {
                notifications.ShowError("Couldn't update your favourite stations", ex);
            }
        }));

        if (Uri.TryCreate(station.Homepage, UriKind.Absolute, out var homepage) && (homepage.Scheme == Uri.UriSchemeHttp || homepage.Scheme == Uri.UriSchemeHttps))
        {
            menu.Items.Add(Item("Open website", "\uE774", async () =>
            {
                try
                {
                    await Windows.System.Launcher.LaunchUriAsync(homepage);
                }
                catch (Exception ex)
                {
                    notifications.ShowError("Couldn't open the website", ex);
                }
            }));
        }

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(Item("Go to Radio", "\uEC05", () => { App.GetService<INavigationService>().NavigateTo(PageKey.Radio); }));
        return menu;
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Style = ItemStyle };
        if (glyph is not null)
        {
            item.Icon = new FontIcon { Glyph = glyph };
        }

        // Every IMediaItemActions / ITrackListHost call reports its own errors, so fire-and-forget is safe.
        item.Click += (_, _) => _ = action();
        return item;
    }

    private static MenuFlyoutItem Item(string text, string? glyph, Action action) =>
        Item(text, glyph, () =>
        {
            action();
            return Task.CompletedTask;
        });
}
