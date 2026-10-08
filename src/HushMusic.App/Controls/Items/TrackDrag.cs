using HushMusic.App.Helpers;
using HushMusic.App.ViewModels.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>Rows and cards that show a hover look and need it cleared after a drag (no PointerExited may follow one).</summary>
internal interface IHoverReset
{
    void ResetHover();
}

/// <summary>
/// <c>items:TrackDrag.IsEnabled="True"</c> on a ListView/GridView makes its track items drag sources
/// (<see cref="TrackDragData"/>): drop them on Up next, the player bar or one of your playlists. Other items (albums,
/// artists, playlists) don't drag. Uses the list's own item dragging, so click, double-click, hover and the context
/// menu behave as before.
/// </summary>
public static class TrackDrag
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(TrackDrag), new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);

    /// <summary>"Song title", or "N songs" for a multi-item drag.</summary>
    internal static string Caption(IReadOnlyList<Track> tracks) => tracks.Count == 1 ? tracks[0].Title : $"{tracks.Count} songs";

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListViewBase list)
        {
            return;
        }

        list.DragItemsStarting -= OnDragItemsStarting;
        list.DragItemsCompleted -= OnDragItemsCompleted;
        list.CanDragItems = e.NewValue is true;
        if (e.NewValue is true)
        {
            list.DragItemsStarting += OnDragItemsStarting;
            list.DragItemsCompleted += OnDragItemsCompleted;
        }
    }

    private static void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        List<Track> tracks = [.. e.Items.Select(AsTrack).OfType<Track>().Where(t => t.IsAvailable)];
        if (tracks.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        TrackDragData.Set(e.Data, tracks, Caption(tracks));
    }

    private static void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        foreach (var item in args.Items)
        {
            if (sender.ContainerFromItem(item) is DependencyObject container && FindHoverable(container) is { } row)
            {
                row.ResetHover();
            }
        }
    }

    private static Track? AsTrack(object? item) => item switch
    {
        Track track => track,
        TrackItem trackItem => trackItem.Track,
        _ => null,
    };

    private static IHoverReset? FindHoverable(DependencyObject container) =>
        (IHoverReset?)VisualTreeSearch.FindDescendant<TrackRow>(container)
        ?? (IHoverReset?)VisualTreeSearch.FindDescendant<QuickPickRow>(container)
        ?? VisualTreeSearch.FindDescendant<MediaCard>(container);
}
