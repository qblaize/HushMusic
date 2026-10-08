using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using HushMusic.App.Services.Shell;
using HushMusic.App.ViewModels.Pages;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// <c>items:TrackSelectionList.Selection="{x:Bind ViewModel.Selection}"</c> on a track ListView: its <see cref="TrackRow"/>s
/// show check marks in select mode, Ctrl+A selects every song and Esc leaves select mode. The page's ItemClick handler
/// calls <see cref="HandleClick"/> first, so Ctrl/Shift-click select instead of playing.
/// </summary>
public static class TrackSelectionList
{
    public static readonly DependencyProperty SelectionProperty = DependencyProperty.RegisterAttached(
        "Selection", typeof(TrackSelection), typeof(TrackSelectionList), new PropertyMetadata(null, OnSelectionChanged));

    // Also when the list handled the key itself.
    private static readonly KeyEventHandler KeyDownHandler = OnKeyDown;

    public static TrackSelection? GetSelection(DependencyObject element) => (TrackSelection?)element.GetValue(SelectionProperty);

    public static void SetSelection(DependencyObject element, TrackSelection? value) => element.SetValue(SelectionProperty, value);

    /// <summary>The selection of the list <paramref name="start"/> is in, if it has one.</summary>
    public static TrackSelection? Find(DependencyObject? start)
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (GetSelection(current) is { } selection)
            {
                return selection;
            }
        }

        return null;
    }

    /// <summary>For the list's ItemClick: true when the click selected or deselected the item (don't play it then).</summary>
    public static bool HandleClick(object sender, object? clickedItem)
    {
        if (sender is not DependencyObject list || GetSelection(list) is not { } selection || clickedItem is not TrackItem item)
        {
            return false;
        }

        var modifiers = ShortcutFocusPolicy.CurrentModifiers();
        return selection.HandleClick(item, modifiers.HasFlag(VirtualKeyModifiers.Control), modifiers.HasFlag(VirtualKeyModifiers.Shift));
    }

    private static void OnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is UIElement element)
        {
            element.RemoveHandler(UIElement.KeyDownEvent, KeyDownHandler);
            if (e.NewValue is not null)
            {
                element.AddHandler(UIElement.KeyDownEvent, KeyDownHandler, handledEventsToo: true);
            }
        }
    }

    private static void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not DependencyObject list || GetSelection(list) is not { } selection || e.OriginalSource is TextBox)
        {
            return;
        }

        var modifiers = ShortcutFocusPolicy.CurrentModifiers();
        if (e.Key == VirtualKey.A && modifiers == VirtualKeyModifiers.Control)
        {
            selection.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None && selection.IsActive)
        {
            selection.Exit();
            e.Handled = true;
        }
    }
}
