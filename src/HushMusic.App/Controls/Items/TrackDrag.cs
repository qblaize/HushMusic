using Windows.ApplicationModel.DataTransfer;
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
/// menu behave as before. Dragging one of several selected songs (<see cref="TrackSelectionList"/>) drags them all; a list
/// with <c>CanReorderItems</c> also gets single songs back as a move.
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
        var selection = sender is DependencyObject list ? TrackSelectionList.GetSelection(list) : null;
        var dragsSelection = selection is { Count: > 1 } && e.Items.Count == 1 && e.Items[0] is TrackItem dragged && selection.IsSelected(dragged);
        var items = dragsSelection ? selection!.SelectedItems.Cast<object>() : e.Items;
        List<Track> tracks = [.. items.Select(AsTrack).OfType<Track>().Where(t => t.IsAvailable)];
        if (tracks.Count == 0)
        {
            e.Cancel = true;
            return;
        }

        TrackDragData.Set(e.Data, tracks, Caption(tracks));

        // Reordering moves the one row the list drags; with a multi-song drag it would move only that one.
        if (!dragsSelection && sender is ListViewBase { CanReorderItems: true })
        {
            e.Data.RequestedOperation = DataPackageOperation.Copy | DataPackageOperation.Move;
        }
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
