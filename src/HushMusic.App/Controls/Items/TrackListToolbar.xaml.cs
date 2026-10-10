using Microsoft.UI.Xaml.Input;
using Windows.System;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Services;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// The filter box and sort menu above a track list (<see cref="TrackListFilter"/>). Esc in the box clears it; Ctrl+F stays
/// the app's search.
/// </summary>
public sealed partial class TrackListToolbar : UserControl
{
    public static readonly DependencyProperty FilterProperty = DependencyProperty.Register(
        nameof(Filter), typeof(TrackListFilter), typeof(TrackListToolbar), new PropertyMetadata(null));

    public TrackListToolbar()
    {
        InitializeComponent();
    }

    public TrackListFilter? Filter
    {
        get => (TrackListFilter?)GetValue(FilterProperty);
        set => SetValue(FilterProperty, value);
    }

    private void OnSortMenuOpening(object? sender, object e)
    {
        if (Filter is not { } filter)
        {
            return;
        }

        CustomItem.Text = filter.OwnOrderName;
        CustomItem.IsChecked = filter.Sort == TrackSort.Custom;
        TitleItem.IsChecked = filter.Sort == TrackSort.Title;
        ArtistItem.IsChecked = filter.Sort == TrackSort.Artist;
        AlbumItem.IsChecked = filter.Sort == TrackSort.Album;
        DurationItem.IsChecked = filter.Sort == TrackSort.Duration;
        AscendingItem.Text = filter.AscendingText;
        DescendingItem.Text = filter.DescendingText;
        AscendingItem.IsChecked = !filter.Descending;
        DescendingItem.IsChecked = filter.Descending;
    }

    private void OnSortClick(object sender, RoutedEventArgs e) => Filter?.SortBy(
        ReferenceEquals(sender, TitleItem) ? TrackSort.Title
        : ReferenceEquals(sender, ArtistItem) ? TrackSort.Artist
        : ReferenceEquals(sender, AlbumItem) ? TrackSort.Album
        : ReferenceEquals(sender, DurationItem) ? TrackSort.Duration
        : TrackSort.Custom);

    private void OnDirectionClick(object sender, RoutedEventArgs e) => Filter?.SetDescending(ReferenceEquals(sender, DescendingItem));

    private void OnFilterKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape && FilterBox.Text.Length > 0)
        {
            FilterBox.Text = string.Empty;
            e.Handled = true;
        }
    }
}
