using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Horizontal row of cards with glass previous/next buttons that fade in on hover. Items use the shared
/// CardTemplateSelector, so a shelf may mix tracks, albums, playlists and artists. With <see cref="ShowAsRows"/> the
/// items are compact song rows in columns of four instead ("Quick picks").
/// </summary>
public sealed partial class Carousel : UserControl
{
    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(object), typeof(Carousel), new PropertyMetadata(null, OnItemsSourceChanged));

    public static readonly DependencyProperty ShowAsRowsProperty = DependencyProperty.Register(
        nameof(ShowAsRows), typeof(bool), typeof(Carousel), new PropertyMetadata(false, OnShowAsRowsChanged));

    // Arrow buttons are 34 px, vertically centred on the artwork (cards) or on the four rows.
    private const double ArrowSize = 34;

    // Shared across carousels so that a fast wheel spin keeps accumulating while the pointer
    // crosses from one carousel to the next.
    private static ScrollViewer? s_wheelTarget;
    private static double s_wheelOffset;
    private static long s_wheelTick;

    private readonly ItemsPanelTemplate _cardsPanel;
    private readonly Thickness _cardsPadding;
    private ScrollViewer? _scroller;
    private UIElement? _wheelPanel;
    private bool _isPointerOver;

    public Carousel()
    {
        InitializeComponent();
        _cardsPanel = List.ItemsPanel;
        _cardsPadding = List.Padding;
    }

    public event ItemClickEventHandler? ItemClick;

    public object? ItemsSource
    {
        get => GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    /// <summary>Set once, in XAML: lay the items out as compact song rows (four per column) instead of cards.</summary>
    public bool ShowAsRows
    {
        get => (bool)GetValue(ShowAsRowsProperty);
        set => SetValue(ShowAsRowsProperty, value);
    }

    private static void OnItemsSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // Carousels are recycled by virtualizing parents: start every new shelf at its beginning.
        var carousel = (Carousel)d;
        carousel._scroller?.ChangeView(0, null, null, disableAnimation: true);
        carousel.UpdateButtons();
    }

    private static void OnShowAsRowsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var carousel = (Carousel)d;
        var resources = Application.Current.Resources;
        var list = carousel.List;
        double contentHeight;
        if (e.NewValue is true)
        {
            list.ItemsPanel = (ItemsPanelTemplate)carousel.Resources["RowsPanel"];
            list.ItemTemplateSelector = null;
            list.ItemTemplate = (DataTemplate)resources["QuickPickRowTemplate"];
            list.ItemContainerStyle = (Style)resources["QuickPickItemContainerStyle"];

            // Rows pad themselves by 8 (hover fill past the art), so the list starts 8 further left to keep the art aligned.
            var padding = carousel._cardsPadding;
            list.Padding = new Thickness(padding.Left - 8, padding.Top, padding.Right, padding.Bottom);
            contentHeight = 4 * (double)resources["QuickPickRowHeight"];
        }
        else
        {
            list.ItemTemplate = null;
            list.ItemTemplateSelector = (DataTemplateSelector)resources["CardTemplateSelector"];
            list.ItemContainerStyle = (Style)resources["CarouselItemContainerStyle"];
            list.ItemsPanel = carousel._cardsPanel;
            list.Padding = carousel._cardsPadding;
            contentHeight = (double)resources["CardArtSize"];
        }

        var top = Math.Round((contentHeight - ArrowSize) / 2);
        carousel.PreviousButton.Margin = new Thickness(-6, top, 0, 0);
        carousel.NextButton.Margin = new Thickness(0, top, -6, 0);
    }

    private void OnListLoaded(object sender, RoutedEventArgs e)
    {
        if (_scroller is null && VisualTreeSearch.FindDescendant<ScrollViewer>(List) is { } scroller)
        {
            _scroller = scroller;
            _scroller.ViewChanged += (_, _) => UpdateButtons();
            _scroller.SizeChanged += (_, _) => UpdateButtons();
        }

        UpdateButtons();
    }

    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!ReferenceEquals(_wheelPanel, List.ItemsPanelRoot) && List.ItemsPanelRoot is { } panel)
        {
            if (_wheelPanel is not null)
            {
                _wheelPanel.PointerWheelChanged -= OnPanelWheelChanged;
            }

            _wheelPanel = panel;
            panel.PointerWheelChanged += OnPanelWheelChanged;
        }
    }

    private void OnListItemClick(object sender, ItemClickEventArgs e)
    {
        CoverAnimation.PrepareFrom(List, e.ClickedItem);
        ItemClick?.Invoke(this, e);
    }

    /// <summary>
    /// A horizontal-only ScrollViewer turns the vertical mouse wheel into horizontal scrolling, which would
    /// trap the page scroll on every carousel. Vertical wheel input is handled here (below the inner
    /// ScrollViewer) and forwarded to the page's ScrollViewer. Shift+wheel and tilt wheels still scroll the row.
    /// </summary>
    private void OnPanelWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsHorizontalMouseWheel || e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift))
        {
            return;
        }

        var outer = VisualTreeSearch.FindAncestor<ScrollViewer>(this);
        if (outer is null)
        {
            return;
        }

        e.Handled = true;
        var now = Environment.TickCount64;
        var from = ReferenceEquals(s_wheelTarget, outer) && now - s_wheelTick < 300 ? s_wheelOffset : outer.VerticalOffset;
        s_wheelOffset = Math.Clamp(from - point.Properties.MouseWheelDelta, 0, outer.ScrollableHeight);
        s_wheelTarget = outer;
        s_wheelTick = now;
        outer.ChangeView(null, s_wheelOffset, null);
    }

    private void OnPreviousClick(object sender, RoutedEventArgs e) => ScrollBy(-1);

    private void OnNextClick(object sender, RoutedEventArgs e) => ScrollBy(1);

    private void ScrollBy(int direction)
    {
        if (_scroller is { } scroller)
        {
            var target = scroller.HorizontalOffset + (direction * scroller.ViewportWidth * 0.8);
            scroller.ChangeView(Math.Clamp(target, 0, scroller.ScrollableWidth), null, null);
        }
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = true;
        UpdateButtons();
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _isPointerOver = false;
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        var scroller = _scroller;
        var canGoBack = scroller is not null && scroller.HorizontalOffset > 1;
        var canGoForward = scroller is not null && scroller.HorizontalOffset < scroller.ScrollableWidth - 1;
        ShowArrow(PreviousButton, canGoBack);
        ShowArrow(NextButton, canGoForward);
    }

    // Arrows that can scroll stay in the tree and fade with the pointer; the others collapse.
    private void ShowArrow(Button arrow, bool canScroll)
    {
        var show = canScroll && _isPointerOver;
        arrow.Visibility = canScroll ? Visibility.Visible : Visibility.Collapsed;
        arrow.IsHitTestVisible = show;

        // Opacity has an implicit transition: only write real changes (this also runs during layout via ItemsSource).
        var opacity = show ? 1.0 : 0.0;
        if (arrow.Opacity != opacity)
        {
            arrow.Opacity = opacity;
        }
    }
}
