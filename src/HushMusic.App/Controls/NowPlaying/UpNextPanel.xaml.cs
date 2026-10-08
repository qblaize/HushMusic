using System.ComponentModel;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using HushMusic.App.Helpers;
using HushMusic.App.ViewModels.Shell;

namespace HushMusic.App.Controls.NowPlaying;

/// <summary>
/// The "Up next" tab. Logic lives in <see cref="QueuePanelViewModel"/>; this is list glue: scrolling to the current
/// song, drag to reorder, drops from pages at the pointer, hover.
/// </summary>
public sealed partial class UpNextPanel : UserControl
{
    private bool _isShown;

    public UpNextPanel()
    {
        InitializeComponent();
        QueueList.AddHandler(DragOverEvent, new DragEventHandler(OnQueueDragOver), handledEventsToo: true);
        QueueList.AddHandler(DropEvent, new DragEventHandler(OnQueueDrop), handledEventsToo: true);
        QueueList.AddHandler(DragLeaveEvent, new DragEventHandler(OnQueueDragLeave), handledEventsToo: true);
        Loaded += (_, _) => ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += (_, _) => ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    public QueuePanelViewModel ViewModel { get; } = App.GetService<QueuePanelViewModel>();

    /// <summary>Songs before the current one are history: drawn quieter.</summary>
    public static double RowOpacity(bool isPlayed) => isPlayed ? 0.5 : 1;

    /// <summary>The tab became visible (true) or hidden (false); visible brings the current song to the top.</summary>
    public void SetShown(bool shown)
    {
        _isShown = shown;
        if (shown)
        {
            // After layout, so the list has its size.
            DispatcherQueue.TryEnqueue(ScrollToCurrent);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(QueuePanelViewModel.Current) && _isShown)
        {
            ScrollToCurrent();
        }
    }

    private void ScrollToCurrent()
    {
        if (ViewModel.Current is { } current)
        {
            QueueList.ScrollIntoView(current, ScrollIntoViewAlignment.Leading);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QueueItemViewModel item)
        {
            item.PlayCommand.Execute(null);
        }
    }

    private void OnListKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Delete && FocusManager.GetFocusedElement(XamlRoot) is ListViewItem { Content: QueueItemViewModel item })
        {
            item.RemoveCommand.Execute(null);
            e.Handled = true;
        }
    }

    // ===== Reorder (the ListView moves the item; the view model mirrors it into the queue) =====

    private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e) =>
        ViewModel.BeginDrag(e.Items.FirstOrDefault() as QueueItemViewModel);

    private void OnDragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args) =>
        ViewModel.CompleteDrag(args.DropResult == DataPackageOperation.Move);

    // ===== Tracks dragged in from pages =====

    private void OnQueueDragOver(object sender, DragEventArgs e)
    {
        // Anything else is the list's own reorder, which it handles itself.
        if (!TrackDragData.Has(e.DataView))
        {
            return;
        }

        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Add to queue";
        e.DragUIOverride.IsGlyphVisible = false;
        ShowDropMarker(InsertionIndex(e));
        e.Handled = true;
    }

    private void OnQueueDragLeave(object sender, DragEventArgs e) => DropMarker.Visibility = Visibility.Collapsed;

    private async void OnQueueDrop(object sender, DragEventArgs e)
    {
        DropMarker.Visibility = Visibility.Collapsed;
        if (!TrackDragData.Has(e.DataView))
        {
            return;
        }

        var index = InsertionIndex(e);
        e.Handled = true;
        var deferral = e.GetDeferral();
        try
        {
            if (await TrackDragData.TryGetAsync(e.DataView) is { Count: > 0 } tracks)
            {
                ViewModel.InsertTracks(tracks, index);
            }
        }
        catch (Exception)
        {
            // The package expired or came from elsewhere: nothing to add.
        }
        finally
        {
            deferral.Complete();
        }
    }

    // The first realized row whose middle is below the pointer; past the last row = the end of the queue.
    private int InsertionIndex(DragEventArgs e)
    {
        var count = ViewModel.Items.Count;
        if (QueueList.ItemsPanelRoot is not { } panel)
        {
            return count;
        }

        var y = e.GetPosition(panel).Y;
        for (var i = 0; i < count; i++)
        {
            if (QueueList.ContainerFromIndex(i) is FrameworkElement row
                && y < row.TransformToVisual(panel).TransformPoint(default).Y + (row.ActualHeight / 2))
            {
                return i;
            }
        }

        return count;
    }

    private void ShowDropMarker(int index)
    {
        var count = ViewModel.Items.Count;
        var anchor = index < count ? QueueList.ContainerFromIndex(index) as FrameworkElement : null;
        var below = anchor is null && count > 0 ? QueueList.ContainerFromIndex(count - 1) as FrameworkElement : null;
        var reference = anchor ?? below;
        if (reference is null)
        {
            DropMarker.Visibility = Visibility.Collapsed;
            return;
        }

        var origin = reference.TransformToVisual(DropLayer).TransformPoint(default);
        var y = anchor is not null ? origin.Y : origin.Y + reference.ActualHeight;
        Canvas.SetLeft(DropMarker, origin.X + 4);
        Canvas.SetTop(DropMarker, Math.Round(y - 1));
        DropMarker.Width = Math.Max(0, reference.ActualWidth - 8);
        DropMarker.Visibility = Visibility.Visible;
    }

    // ===== Hover swaps the duration for a remove button =====

    private void OnRowPointerEntered(object sender, PointerRoutedEventArgs e) => SetRowHover(sender as FrameworkElement, true);

    private void OnRowPointerExited(object sender, PointerRoutedEventArgs e) => SetRowHover(sender as FrameworkElement, false);

    // Recycled containers must not keep a previous row's hover state.
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        SetRowHover(args.ItemContainer?.ContentTemplateRoot as FrameworkElement, false);

    private static void SetRowHover(FrameworkElement? row, bool hover)
    {
        if (row?.FindName("RemoveButton") is UIElement remove && row.FindName("DurationLabel") is UIElement duration)
        {
            remove.Visibility = hover ? Visibility.Visible : Visibility.Collapsed;
            duration.Visibility = hover ? Visibility.Collapsed : Visibility.Visible;
        }
    }
}
