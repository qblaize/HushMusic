using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using HushMusic.App.Services.Pages;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// One track in a list: art (or track number), title, explicit badge, artist links, album link, like heart, duration
/// and a "more" menu. The heart and "more" appear on hover; the playing track gets an accent title and an equaliser.
/// <see cref="Item"/> accepts a <see cref="Track"/> or a <see cref="TrackItem"/>. In a list with a
/// <see cref="TrackSelectionList.SelectionProperty"/>, select mode adds a check mark in front of the row.
/// </summary>
public sealed partial class TrackRow : UserControl, IHoverReset
{
    private const double MinWidthForAlbum = 640;
    private const double MinWidthForLike = 420;

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(object), typeof(TrackRow), new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty CurrentTrackProperty = DependencyProperty.Register(
        nameof(CurrentTrack), typeof(object), typeof(TrackRow), new PropertyMetadata(null));

    public static readonly DependencyProperty NumberProperty = DependencyProperty.Register(
        nameof(Number), typeof(int), typeof(TrackRow), new PropertyMetadata(0));

    public static readonly DependencyProperty ShowNumberProperty = DependencyProperty.Register(
        nameof(ShowNumber), typeof(bool), typeof(TrackRow), new PropertyMetadata(false, OnLayoutChanged));

    public static readonly DependencyProperty ShowAlbumProperty = DependencyProperty.Register(
        nameof(ShowAlbum), typeof(bool), typeof(TrackRow), new PropertyMetadata(true, OnLayoutChanged));

    private ILikeStateService? _likes;
    private INowPlayingService? _nowPlaying;
    private TrackSelection? _selection;
    private bool _isLiked;
    private bool _isHovered;
    private bool _isCurrent;

    public TrackRow()
    {
        InitializeComponent();
    }

    public object? Item
    {
        get => GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    // Internal: a public Track-typed member would make the XAML compiler emit an activator for Track.
    internal Track? CurrentTrack
    {
        get => GetValue(CurrentTrackProperty) as Track;
        private set => SetValue(CurrentTrackProperty, value);
    }

    internal int Number
    {
        get => (int)GetValue(NumberProperty);
        private set => SetValue(NumberProperty, value);
    }

    /// <summary>Shows the track number instead of the art (album pages).</summary>
    public bool ShowNumber
    {
        get => (bool)GetValue(ShowNumberProperty);
        set => SetValue(ShowNumberProperty, value);
    }

    public bool ShowAlbum
    {
        get => (bool)GetValue(ShowAlbumProperty);
        set => SetValue(ShowAlbumProperty, value);
    }

    private static IStreamWarmup Warmup => App.GetService<IStreamWarmup>();

    private static IMediaItemActions Actions => App.GetService<IMediaItemActions>();

    private ILikeStateService Likes => _likes ??= App.GetService<ILikeStateService>();

    private INowPlayingService NowPlaying => _nowPlaying ??= App.GetService<INowPlayingService>();

    /// <summary>Drops the hover look (the row was recycled, or a drag took the pointer away).</summary>
    public void ResetHover()
    {
        if (_isHovered)
        {
            _isHovered = false;
            UpdateHoverChrome();
            Warmup.HoverEnded(CurrentTrack);
        }
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (TrackRow)d;
        (row.CurrentTrack, row.Number) = e.NewValue switch
        {
            TrackItem item => (item.Track, item.Number),
            Track track => (track, 0),
            _ => ((Track?)null, 0),
        };
        row._isHovered = false;
        row.UpdateLiked();
        row.UpdateNowPlaying();
        row.UpdateSelection();
    }

    private static void OnLayoutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (TrackRow)d;
        row.UpdateLayoutColumns(row.ActualWidth);
        row.UpdateNowPlaying();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Likes.Changed += OnTrackRated;
        NowPlaying.Changed += OnNowPlayingChanged;
        _selection = TrackSelectionList.Find(this);
        if (_selection is not null)
        {
            _selection.Changed += OnSelectionChanged;
        }

        UpdateLiked();
        UpdateNowPlaying();
        UpdateSelection();
        UpdateLayoutColumns(ActualWidth);

        // A true hairline: one physical pixel at any display scale.
        if (XamlRoot is { RasterizationScale: > 0 } root)
        {
            Hairline.Height = 1 / root.RasterizationScale;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        Likes.Changed -= OnTrackRated;
        NowPlaying.Changed -= OnNowPlayingChanged;
        if (_selection is not null)
        {
            _selection.Changed -= OnSelectionChanged;
            _selection = null;
        }
    }

    private void OnTrackRated(object? sender, TrackRatedEventArgs e)
    {
        if (CurrentTrack?.VideoId == e.VideoId)
        {
            _isLiked = e.Status == LikeStatus.Like;
            UpdateHoverChrome();
        }
    }

    private void OnNowPlayingChanged(object? sender, EventArgs e) => UpdateNowPlaying();

    private void OnSelectionChanged(object? sender, EventArgs e) => UpdateSelection();

    private void UpdateSelection()
    {
        var item = Item as TrackItem;
        var selectable = item is not null && _selection is { IsActive: true };
        var selected = selectable && _selection!.IsSelected(item!);
        if (selectable && SelectCheck is null)
        {
            FindName(nameof(SelectCheck));
        }

        VisualStateManager.GoToState(this, !selectable ? "NotSelectable" : selected ? "Selected" : "Selectable", false);

        var name = CurrentTrack is { } track ? $"{track.Title}, {track.ArtistsText}" : string.Empty;
        AutomationProperties.SetName(this, selected ? name + ", selected" : name);
        if (SelectCheck is { } check)
        {
            var label = selected ? "Deselect" : "Select";
            AutomationProperties.SetName(check, CurrentTrack is { } current ? $"{label} {current.Title}" : label);
        }
    }

    // Shift extends the selection from the last clicked row, like Shift-clicking the row itself.
    private void OnSelectCheckClick(object sender, RoutedEventArgs e)
    {
        if (_selection is not { } selection || Item is not TrackItem item)
        {
            return;
        }

        var modifiers = ShortcutFocusPolicy.CurrentModifiers();
        if (modifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            selection.SelectRange(item, keepOthers: modifiers.HasFlag(VirtualKeyModifiers.Control));
        }
        else
        {
            selection.Toggle(item);
        }
    }

    private void UpdateLiked()
    {
        _isLiked = CurrentTrack is { } track && Likes.GetStatus(track) == LikeStatus.Like;
        UpdateHoverChrome();
    }

    private void UpdateNowPlaying()
    {
        var isCurrent = CurrentTrack is { } track && NowPlaying.IsCurrent(track.VideoId);
        if (isCurrent && Equalizer is null)
        {
            FindName(nameof(Equalizer));
        }

        _isCurrent = isCurrent;

        // Bars sit on the darkened artwork in art rows, and in place of the number on album rows.
        var state = !isCurrent ? "NotCurrent" : ShowNumber ? "CurrentOnNumber" : "CurrentOnArt";
        VisualStateManager.GoToState(this, state, false);
        if (Equalizer is { } equalizer)
        {
            equalizer.IsPlaying = isCurrent && NowPlaying.IsPlaying;
            equalizer.Refresh();
        }
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => UpdateLayoutColumns(e.NewSize.Width);

    private void UpdateLayoutColumns(double width)
    {
        ArtHost.Visibility = ShowNumber ? Visibility.Collapsed : Visibility.Visible;
        NumberText.Visibility = ShowNumber ? Visibility.Visible : Visibility.Collapsed;

        var showAlbum = ShowAlbum && (width <= 0 || width >= MinWidthForAlbum);
        AlbumColumn.Width = showAlbum ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
        AlbumText.Visibility = showAlbum ? Visibility.Visible : Visibility.Collapsed;

        var showLike = Actions.IsSignedIn && (width <= 0 || width >= MinWidthForLike);
        LikeColumn.Width = new GridLength(showLike ? 32 : 0);
        LikeButton.Visibility = showLike ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        UpdateHoverChrome();
        Warmup.HoverStarted(CurrentTrack);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        UpdateHoverChrome();
        Warmup.HoverEnded(CurrentTrack);
    }

    private void UpdateHoverChrome()
    {
        // Like and "more" appear on hover; the heart is filled (accent) when the song is liked.
        VisualStateManager.GoToState(this, _isHovered ? "PointerOver" : "Normal", false);
        VisualStateManager.GoToState(this, _isLiked ? "Liked" : "NotLiked", false);

        var label = _isLiked ? "Remove like" : "Like";
        AutomationProperties.SetName(LikeButton, label);
        ToolTipService.SetToolTip(LikeButton, label);
    }

    private void OnLikeClick(object sender, RoutedEventArgs e)
    {
        if (CurrentTrack is { } track)
        {
            _ = Actions.RateAsync(track, _isLiked ? LikeStatus.Indifferent : LikeStatus.Like);
        }
    }

    private void OnAlbumClick(Microsoft.UI.Xaml.Documents.Hyperlink sender, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs args) =>
        Actions.OpenAlbum(CurrentTrack?.Album?.BrowseId);

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (CurrentTrack is { } track)
        {
            TrackMenu.ShowAt(MoreButton, track, Item as TrackItem);
        }
    }
}
