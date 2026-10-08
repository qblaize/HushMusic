using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using HushMusic.App.Services.Pages;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// One song in a "Quick picks" shelf (a horizontal grid of compact rows, see <see cref="Carousel.ShowAsRows"/>).
/// Hover reveals a play glyph on the art and the "more" button; the playing song gets an accent title and an equaliser.
/// <see cref="Item"/> accepts a <see cref="Track"/> or a <see cref="TrackItem"/>.
/// </summary>
public sealed partial class QuickPickRow : UserControl, IHoverReset
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(object), typeof(QuickPickRow), new PropertyMetadata(null, OnItemChanged));

    public static readonly DependencyProperty CurrentTrackProperty = DependencyProperty.Register(
        nameof(CurrentTrack), typeof(object), typeof(QuickPickRow), new PropertyMetadata(null));

    private INowPlayingService? _nowPlaying;
    private bool _isHovered;
    private bool _isCurrent;

    public QuickPickRow()
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

    private static IStreamWarmup Warmup => App.GetService<IStreamWarmup>();

    private INowPlayingService NowPlaying => _nowPlaying ??= App.GetService<INowPlayingService>();

    /// <summary>Drops the hover look (the row was recycled, or a drag took the pointer away).</summary>
    public void ResetHover()
    {
        if (_isHovered)
        {
            _isHovered = false;
            UpdateState();
            Warmup.HoverEnded(CurrentTrack);
        }
    }

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (QuickPickRow)d;
        row.CurrentTrack = e.NewValue switch
        {
            TrackItem item => item.Track,
            Track track => track,
            _ => null,
        };
        row._isHovered = false;
        row.UpdateNowPlaying();
        AutomationProperties.SetName(row, row.CurrentTrack is { } current ? $"{current.Title}, {current.ArtistsText}" : string.Empty);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        NowPlaying.Changed += OnNowPlayingChanged;
        UpdateNowPlaying();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => NowPlaying.Changed -= OnNowPlayingChanged;

    private void OnNowPlayingChanged(object? sender, EventArgs e) => UpdateNowPlaying();

    private void UpdateNowPlaying()
    {
        _isCurrent = CurrentTrack is { } track && NowPlaying.IsCurrent(track.VideoId);
        if (_isCurrent && Equalizer is null)
        {
            FindName(nameof(Equalizer));
        }

        UpdateState();
    }

    private void UpdateState()
    {
        var state = (_isCurrent, _isHovered) switch
        {
            (true, true) => "CurrentPointerOver",
            (true, false) => "Current",
            (false, true) => "PointerOver",
            _ => "Normal",
        };
        VisualStateManager.GoToState(this, state, false);

        if (Equalizer is { } equalizer)
        {
            equalizer.IsPlaying = _isCurrent && NowPlaying.IsPlaying;
            equalizer.Refresh();
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = true;
        UpdateState();
        Warmup.HoverStarted(CurrentTrack);
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isHovered = false;
        UpdateState();
        Warmup.HoverEnded(CurrentTrack);
    }

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (CurrentTrack is { } track)
        {
            TrackMenu.ShowAt(MoreButton, track);
        }
    }
}
