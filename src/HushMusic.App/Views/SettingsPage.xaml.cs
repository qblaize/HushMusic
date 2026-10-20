using Microsoft.UI.Xaml.Navigation;

namespace HushMusic.App.Views;

public sealed partial class SettingsPage : Page
{
    private MenuFlyout? _lyricsSourceMenu;

    public SettingsPage()
    {
        InitializeComponent();

        // Automated screenshots can't scroll: HUSHMUSIC_TEST_SETTINGS_SCROLL=end keeps the page at the bottom (About),
        // =lyrics at the Lyrics section.
        switch (Environment.GetEnvironmentVariable("HUSHMUSIC_TEST_SETTINGS_SCROLL"))
        {
            case "end":
                Scroller.SizeChanged += (_, _) => ScrollToEnd();
                ((FrameworkElement)Scroller.Content).SizeChanged += (_, _) => ScrollToEnd();
                break;
            case "lyrics":
                ((FrameworkElement)Scroller.Content).SizeChanged += (_, _) => ScrollToLyrics();
                break;
        }
    }

    public SettingsViewModel ViewModel { get; } = App.GetService<SettingsViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e) => _ = ViewModel.OnNavigatedToAsync(e.Parameter);

    protected override void OnNavigatedFrom(NavigationEventArgs e) => ViewModel.OnNavigatedFrom();

    private void ScrollToEnd() =>
        DispatcherQueue.TryEnqueue(() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, disableAnimation: true));

    private void ScrollToLyrics() => DispatcherQueue.TryEnqueue(() =>
    {
        var top = LyricsSectionHeader.TransformToVisual((UIElement)Scroller.Content).TransformPoint(default).Y;
        Scroller.ChangeView(null, Math.Max(0, top - 16), null, disableAnimation: true);
    });

    // ===== Lyrics sources: drag to reorder, or Move up / Move down from a row's context menu =====

    private void OnLyricsSourcesDragged(ListViewBase sender, DragItemsCompletedEventArgs args) => ViewModel.CommitLyricsSourceOrder();

    // The menu sits on the row container, where right-click and Shift+F10 / the menu key on a focused row both open it.
    private void OnLyricsSourceContainerChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        args.ItemContainer.ContextFlyout ??= LyricsSourceMenu();

    private MenuFlyout LyricsSourceMenu()
    {
        if (_lyricsSourceMenu is not null)
        {
            return _lyricsSourceMenu;
        }

        var up = new MenuFlyoutItem { Text = "Move up", Icon = new FontIcon { Glyph = "\uE74A" } };
        var down = new MenuFlyoutItem { Text = "Move down", Icon = new FontIcon { Glyph = "\uE74B" } };
        var menu = new MenuFlyout { Items = { up, down } };
        menu.Opening += (_, _) =>
        {
            var item = SourceOf(menu);
            up.IsEnabled = item?.CanMoveUp == true;
            down.IsEnabled = item?.CanMoveDown == true;
        };
        up.Click += (_, _) => MoveLyricsSource(SourceOf(menu), -1);
        down.Click += (_, _) => MoveLyricsSource(SourceOf(menu), 1);
        _lyricsSourceMenu = menu;
        return menu;
    }

    private static LyricsSourceItem? SourceOf(MenuFlyout menu) => (menu.Target as ListViewItem)?.Content as LyricsSourceItem;

    // Keyboard focus follows the moved row.
    private void MoveLyricsSource(LyricsSourceItem? item, int delta)
    {
        if (item is null)
        {
            return;
        }

        ViewModel.MoveLyricsSource(item, delta);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (LyricsSourcesList.ContainerFromItem(item) is ListViewItem container)
            {
                container.Focus(FocusState.Keyboard);
            }
        });
    }
}
