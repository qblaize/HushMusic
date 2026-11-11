using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using HushMusic.App.Services.Pages;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// The context menu of album, playlist and artist cards and rows (right click, Shift+F10 or the menu key): Play for
/// albums and playlists, and "Pin to sidebar" / "Unpin from sidebar". <c>CollectionMenu.Item</c> on the card or row
/// gives it the menu; songs use <see cref="TrackMenu"/> instead, and mixes (which can't be pinned) get none.
/// </summary>
public static class CollectionMenu
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.RegisterAttached(
        "Item", typeof(object), typeof(CollectionMenu), new PropertyMetadata(null, OnItemChanged));

    public static object? GetItem(DependencyObject element) => element.GetValue(ItemProperty);

    public static void SetItem(DependencyObject element, object? value) => element.SetValue(ItemProperty, value);

    private static void OnItemChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
        {
            return;
        }

        element.ContextRequested -= OnContextRequested;
        if (SidebarPinItems.For(e.NewValue as MediaItem) is not null)
        {
            element.ContextRequested += OnContextRequested;
        }
    }

    private static void OnContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (GetItem(sender) is not MediaItem item || SidebarPinItems.For(item) is not { } pin || sender is not FrameworkElement target)
        {
            return;
        }

        args.Handled = true;
        var menu = Create(item, pin);
        if (args.TryGetPosition(target, out var point))
        {
            menu.ShowAt(target, new FlyoutShowOptions { Position = point });
        }
        else
        {
            menu.ShowAt(target);
        }
    }

    private static MenuFlyout Create(MediaItem item, SidebarPin pin)
    {
        var actions = App.GetService<IMediaItemActions>();
        var pins = App.GetService<ISidebarPinActions>();
        var menu = new MenuFlyout { MenuFlyoutPresenterStyle = TrackMenu.PresenterStyle };
        if (item is Album or Playlist)
        {
            menu.Items.Add(Item("Play", "", () => actions.PlayAsync(item)));
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        var isPinned = pins.IsPinned(pin);
        var pinItem = Item(SidebarPinItems.Label(isPinned), SidebarPinItems.Glyph(isPinned), () => pins.ToggleAsync(pin));
        AutomationProperties.SetAutomationId(pinItem, "PinToSidebar");
        menu.Items.Add(pinItem);
        return menu;
    }

    // Every action reports its own errors, so fire-and-forget is safe.
    private static MenuFlyoutItem Item(string text, string glyph, Func<Task> action)
    {
        var item = new MenuFlyoutItem { Text = text, Style = TrackMenu.ItemStyle, Icon = new FontIcon { Glyph = glyph } };
        item.Click += (_, _) => _ = action();
        return item;
    }
}
