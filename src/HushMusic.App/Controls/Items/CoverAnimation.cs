using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// "Cover zoom": a connected animation from a card's artwork (album, playlist, single) to the detail page's header
/// art, and back to the card when going Back while that card is still realized and on screen.
/// Every step is optional: when anything doesn't line up (card virtualized, art not loaded, the page was opened
/// another way, animations turned off) the animation is cancelled and navigation looks as usual.
/// </summary>
internal static class CoverAnimation
{
    private const string ForwardKey = "CoverZoom";
    private const string BackKey = "CoverZoomBack";
    private const long MaxPendingMilliseconds = 2000;
    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(300);

    private static string? s_forwardId;
    private static long s_forwardTick;
    private static string? s_backId;
    private static long s_backTick;

    // The card the current detail page was opened from (for the way back).
    private static WeakReference<ListViewBase>? s_sourceList;
    private static object? s_sourceItem;
    private static string? s_sourceId;

    private static bool Enabled => Skeleton.AnimationsEnabled;

    /// <summary>From an ItemClick handler, before the item is opened: lifts the clicked card's art off the page.</summary>
    public static void PrepareFrom(ListViewBase list, object? item)
    {
        var id = NavigationPreviews.IdOf(item);
        if (!Enabled || id is null || item is Artist)
        {
            return;
        }

        if (list.ContainerFromItem(item) is not SelectorItem container
            || VisualTreeSearch.FindDescendant<MediaCard>(container) is not { IsCircular: false, IsArtLoaded: true } card)
        {
            return;
        }

        if (!Try(() => Service().PrepareToAnimate(ForwardKey, card.ArtElement)))
        {
            return;
        }

        s_forwardId = id;
        s_forwardTick = Environment.TickCount64;
        s_sourceList = new WeakReference<ListViewBase>(list);
        s_sourceItem = item;
        s_sourceId = id;
    }

    /// <summary>Detail page, OnNavigatedTo of a new visit: flies the card art into the header.</summary>
    public static void TryStartForward(string? id, DetailHeader header)
    {
        var pendingId = s_forwardId;
        var fresh = Environment.TickCount64 - s_forwardTick < MaxPendingMilliseconds;
        s_forwardId = null;
        Try(() =>
        {
            if (Service().GetAnimation(ForwardKey) is not { } animation)
            {
                return;
            }

            if (!Enabled || id is null || id != pendingId || !fresh)
            {
                animation.Cancel();
                return;
            }

            animation.Configuration = new BasicConnectedAnimationConfiguration();
            animation.TryStart(header.ArtElement);
        });
    }

    /// <summary>Detail page, OnNavigatingFrom when going Back: takes the header art along to the card it came from.</summary>
    public static void PrepareBack(string? id, DetailHeader header)
    {
        if (!Enabled || id is null || id != s_sourceId || !header.IsArtLoaded)
        {
            return;
        }

        if (Try(() => Service().PrepareToAnimate(BackKey, header.ArtElement)))
        {
            s_backId = id;
            s_backTick = Environment.TickCount64;
        }
    }

    /// <summary>Page with cards, OnNavigatedTo when going Back: lands the header art on its card, once the page is laid out.</summary>
    public static void TryStartBack(Page page)
    {
        var backId = s_backId;
        s_backId = null;
        if (backId is null)
        {
            return;
        }

        if (page.IsLoaded)
        {
            page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => StartBack(page, backId));
            return;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            page.Loaded -= OnLoaded;
            page.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => StartBack(page, backId));
        }

        page.Loaded += OnLoaded;
    }

    private static void StartBack(Page page, string backId) => Try(() =>
    {
        if (Service().GetAnimation(BackKey) is not { } animation)
        {
            return;
        }

        var card = Environment.TickCount64 - s_backTick < MaxPendingMilliseconds && backId == s_sourceId ? FindVisibleSourceCard(page) : null;
        s_sourceList = null;
        s_sourceItem = null;
        s_sourceId = null;
        if (card is null)
        {
            animation.Cancel();
            return;
        }

        animation.Configuration = new DirectConnectedAnimationConfiguration();
        animation.TryStart(card.ArtElement);
    });

    // The source card only counts while it is realized, in this page's tree and inside both the list's and the page's viewport.
    private static MediaCard? FindVisibleSourceCard(Page page)
    {
        if (s_sourceList?.TryGetTarget(out var list) != true || list is not { IsLoaded: true } || s_sourceItem is null
            || list.XamlRoot != page.XamlRoot || list.ContainerFromItem(s_sourceItem) is not SelectorItem container
            || VisualTreeSearch.FindDescendant<MediaCard>(container) is not { IsLoaded: true } card)
        {
            return null;
        }

        var bounds = new Rect(0, 0, card.ArtElement.ActualSize.X, card.ArtElement.ActualSize.Y);
        return IsInside(card.ArtElement, bounds, list) && IsInside(card.ArtElement, bounds, page) ? card : null;
    }

    private static bool IsInside(UIElement element, Rect bounds, FrameworkElement viewport)
    {
        var rect = element.TransformToVisual(viewport).TransformBounds(bounds);
        var center = new Point(rect.X + (rect.Width / 2), rect.Y + (rect.Height / 2));
        return center.X >= 0 && center.Y >= 0 && center.X <= viewport.ActualWidth && center.Y <= viewport.ActualHeight;
    }

    private static ConnectedAnimationService Service()
    {
        var service = ConnectedAnimationService.GetForCurrentView();
        service.DefaultDuration = Duration;
        return service;
    }

    // A cosmetic animation must never take navigation down with it.
    private static bool Try(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            App.GetService<ILogger<DetailHeader>>().LogDebug(ex, "Cover animation skipped");
            return false;
        }
    }
}
