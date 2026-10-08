using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace HushMusic.App.Services.Shell;

/// <summary>What currently has keyboard focus, as far as global shortcuts care.</summary>
public enum ShortcutFocus
{
    /// <summary>Nothing that uses the key itself: shortcuts apply.</summary>
    None,

    /// <summary>An editable text control: Space types, Ctrl+Left/Right move by word.</summary>
    TextInput,

    /// <summary>A control the user tabbed to that activates on Space (button, toggle, list item...).</summary>
    KeyboardActivatable,
}

/// <summary>Reads focus and modifier state for <see cref="ViewModels.ShellViewModel.HandleShortcut"/>. UI thread only.</summary>
public static class ShortcutFocusPolicy
{
    public static ShortcutFocus Classify(XamlRoot? root)
    {
        if (root is null)
        {
            return ShortcutFocus.None;
        }

        var focused = FocusManager.GetFocusedElement(root);
        if (focused is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or NumberBox || focused is ComboBox { IsEditable: true })
        {
            return ShortcutFocus.TextInput;
        }

        // A button clicked with the mouse keeps focus; Space should still toggle playback then,
        // but a control reached with Tab keeps its native Space behaviour.
        if (focused is Control { FocusState: FocusState.Keyboard } and (ButtonBase or ToggleSwitch or ComboBox or SelectorItem or NavigationViewItemBase or ItemContainer))
        {
            return ShortcutFocus.KeyboardActivatable;
        }

        return ShortcutFocus.None;
    }

    public static VirtualKeyModifiers CurrentModifiers()
    {
        var modifiers = VirtualKeyModifiers.None;
        if (IsDown(VirtualKey.Control))
        {
            modifiers |= VirtualKeyModifiers.Control;
        }

        if (IsDown(VirtualKey.Shift))
        {
            modifiers |= VirtualKeyModifiers.Shift;
        }

        if (IsDown(VirtualKey.Menu))
        {
            modifiers |= VirtualKeyModifiers.Menu;
        }

        if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows))
        {
            modifiers |= VirtualKeyModifiers.Windows;
        }

        return modifiers;
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
}
