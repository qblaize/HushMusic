using System.Numerics;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>Square (or round, or 16:9) art card for albums, playlists, artists and tracks in carousels and grids.</summary>
public sealed partial class MediaCard : UserControl, IHoverReset
{
    // 16:9 at the shared card height.
    private const double WideAspect = 16.0 / 9.0;
    private const float HoverScale = 1.03f;
    private static readonly TimeSpan HoverDuration = TimeSpan.FromMilliseconds(150);

    // Elevation (Z) is what makes ThemeShadow cast; the small Y lift reads as "picked up".
    private static readonly Vector3 HoverTranslation = new(0, -2, 24);

    private static bool? s_animationsEnabled;

    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(object), typeof(MediaCard), new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty IsCircularProperty = DependencyProperty.Register(
        nameof(IsCircular), typeof(bool), typeof(MediaCard), new PropertyMetadata(false, OnShapeChanged));

    public static readonly DependencyProperty IsWideProperty = DependencyProperty.Register(
        nameof(IsWide), typeof(bool), typeof(MediaCard), new PropertyMetadata(false, OnShapeChanged));

    public static readonly DependencyProperty AcceptsTrackDropsProperty = DependencyProperty.Register(
        nameof(AcceptsTrackDrops), typeof(bool), typeof(MediaCard), new PropertyMetadata(false, OnAcceptsTrackDropsChanged));

    private readonly CornerRadius _defaultCorner;
    private bool _isHovered;

    public MediaCard()
    {
        InitializeComponent();
        _defaultCorner = ArtHost.CornerRadius;
        if (AnimationsEnabled)
        {
            ArtHost.ScaleTransition = new Vector3Transition { Duration = HoverDuration };
            ArtHost.TranslationTransition = new Vector3Transition { Duration = HoverDuration };
        }
    }

    public MediaItem? Item
    {
        get => GetValue(ItemProperty) as MediaItem;
        set => SetValue(ItemProperty, value);
    }

    /// <summary>Round art and centered text, used for artists.</summary>
    public bool IsCircular
    {
        get => (bool)GetValue(IsCircularProperty);
        set => SetValue(IsCircularProperty, value);
    }

    /// <summary>16:9 art, used for music videos.</summary>
    public bool IsWide
    {
        get => (bool)GetValue(IsWideProperty);
        set => SetValue(IsWideProperty, value);
    }

    /// <summary>Playlist cards that take dragged songs (your own playlists only; see <see cref="IPlaylistDropTarget"/>).</summary>
    public bool AcceptsTrackDrops
    {
        get => (bool)GetValue(AcceptsTrackDropsProperty);
        set => SetValue(AcceptsTrackDropsProperty, value);
    }

    /// <summary>The artwork element (cover-zoom connected animation source/target).</summary>
    internal UIElement ArtElement => ArtHost;

    /// <summary>True once the artwork has loaded (not the placeholder glyph).</summary>
    internal bool IsArtLoaded => ArtImage.Source is BitmapImage { PixelWidth: > 0 };

    private bool CanPlay => Item is Track or Album or Playlist;

    private static IStreamWarmup Warmup => App.GetService<IStreamWarmup>();

    private static IPlaylistDropTarget DropTarget => App.GetService<IPlaylistDropTarget>();

    // Honours "Animation effects" in Windows settings.
    private static bool AnimationsEnabled => s_animationsEnabled ??= new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        var item = e.NewValue as MediaItem;
        TrackMenu.SetTrack(card, item as Track);
        AutomationProperties.SetName(card, item is null ? string.Empty : $"{ItemFormat.Kind(item)}: {item.Title}");
        card.PlayButton.Visibility = card.CanPlay ? Visibility.Visible : Visibility.Collapsed;

        // Item changes happen inside layout (container recycling); only touch the transitioned properties when a
        // recycled card is still lifted. Starting implicit transitions during a layout pass crashes WinUI natively.
        if (card._isHovered)
        {
            card.SetHover(false);
        }
    }

    private static void OnShapeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        var size = card.ArtHost.Height;
        var width = card.IsWide ? Math.Round(size * WideAspect) : size;
        card.Root.Width = width;
        card.ArtHost.Width = width;
        card.ArtHost.CornerRadius = card.IsCircular ? new CornerRadius(size / 2) : card._defaultCorner;
        var alignment = card.IsCircular ? TextAlignment.Center : TextAlignment.Left;
        card.TitleText.TextAlignment = alignment;
        card.SubtitleText.TextAlignment = alignment;
    }

    private static void OnAcceptsTrackDropsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (MediaCard)d;
        card.AllowDrop = e.NewValue is true;
        card.DragEnter -= card.OnTrackDragOver;
        card.DragOver -= card.OnTrackDragOver;
        card.DragLeave -= card.OnTrackDragLeave;
        card.Drop -= card.OnTrackDrop;
        if (e.NewValue is true)
        {
            card.DragEnter += card.OnTrackDragOver;
            card.DragOver += card.OnTrackDragOver;
            card.DragLeave += card.OnTrackDragLeave;
            card.Drop += card.OnTrackDrop;
        }
    }

    private void OnTrackDragOver(object sender, DragEventArgs e)
    {
        // While ownership is still being checked the drop is refused; DragOver repeats and picks up the answer.
        var accepts = Item is Playlist playlist && TrackDragData.Has(e.DataView) && DropTarget.GetState(playlist) == PlaylistDropState.Accepts;
        e.AcceptedOperation = accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
        if (accepts)
        {
            e.DragUIOverride.Caption = $"Add to {Item!.Title}";
            e.DragUIOverride.IsCaptionVisible = true;
            e.DragUIOverride.IsGlyphVisible = false;
        }

        ShowDropTarget(accepts);
        e.Handled = true;
    }

    private void OnTrackDragLeave(object sender, DragEventArgs e) => ShowDropTarget(false);

    private async void OnTrackDrop(object sender, DragEventArgs e)
    {
        ShowDropTarget(false);
        if (Item is not Playlist playlist || DropTarget.GetState(playlist) != PlaylistDropState.Accepts)
        {
            return;
        }

        e.Handled = true;
        var deferral = e.GetDeferral();
        IReadOnlyList<Track>? tracks;
        try
        {
            tracks = await TrackDragData.TryGetAsync(e.DataView);
        }
        catch (Exception)
        {
            tracks = null;
        }
        finally
        {
            deferral.Complete();
        }

        if (tracks is { Count: > 0 })
        {
            await DropTarget.AddAsync(playlist, tracks);
        }
    }

    private void ShowDropTarget(bool show)
    {
        DropRing.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        SetHover(show);
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        SetHover(true);
        Warmup.HoverStarted(Item as Track);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        SetHover(false);
        Warmup.HoverEnded(Item as Track);
    }

    private void SetHover(bool hover)
    {
        if (hover == _isHovered)
        {
            return;
        }

        _isHovered = hover;
        ArtHost.CenterPoint = new Vector3((float)(ArtHost.Width / 2), (float)(ArtHost.Height / 2), 0);
        ArtHost.Scale = hover ? new Vector3(HoverScale, HoverScale, 1) : Vector3.One;
        ArtHost.Translation = hover ? HoverTranslation : Vector3.Zero;

        Scrim.Opacity = hover ? 1 : 0;
        var showPlay = hover && CanPlay;
        PlayButton.Opacity = showPlay ? 1 : 0;
        PlayButton.IsHitTestVisible = showPlay;
    }

    /// <summary>Drops the hover lift (a drag took the pointer away without a PointerExited).</summary>
    public void ResetHover()
    {
        if (_isHovered)
        {
            SetHover(false);
            Warmup.HoverEnded(Item as Track);
        }
    }

    private void OnPlayClick(object sender, RoutedEventArgs e) => _ = App.GetService<IMediaItemActions>().PlayAsync(Item);
}
