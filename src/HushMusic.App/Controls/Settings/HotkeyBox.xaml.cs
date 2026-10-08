using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Services;

namespace HushMusic.App.Controls.Settings;

/// <summary>
/// Records a global shortcut: select it, then press the keys. A key needs Ctrl, Alt or Win (or Shift with a key that
/// doesn't type); Esc cancels and Backspace removes the shortcut. Two-way bind <see cref="Gesture"/> (empty = none) and
/// <see cref="IsCapturing"/>.
/// <para>
/// While recording, the keys are taken at the top of the window (a tunnelling handler on the window content), so the
/// app's own shortcuts (Space, Ctrl+F, Alt+Left…) don't run on the way to this box.
/// </para>
/// </summary>
public sealed partial class HotkeyBox : UserControl
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyBox), new PropertyMetadata(string.Empty, (d, _) => ((HotkeyBox)d).Show()));

    public static readonly DependencyProperty IsCapturingProperty = DependencyProperty.Register(
        nameof(IsCapturing), typeof(bool), typeof(HotkeyBox), new PropertyMetadata(false));

    public static readonly DependencyProperty ActionNameProperty = DependencyProperty.Register(
        nameof(ActionName), typeof(string), typeof(HotkeyBox), new PropertyMetadata(string.Empty, (d, _) => ((HotkeyBox)d).Show()));

    private readonly KeyEventHandler _keyDown;
    private readonly KeyEventHandler _keyUp;
    private UIElement? _keySource;

    public HotkeyBox()
    {
        InitializeComponent();
        _keyDown = OnWindowPreviewKeyDown;
        _keyUp = OnWindowPreviewKeyUp;
        Unloaded += (_, _) => EndCapture();
        Show();
    }

    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    public bool IsCapturing
    {
        get => (bool)GetValue(IsCapturingProperty);
        set => SetValue(IsCapturingProperty, value);
    }

    /// <summary>What the shortcut does ("Next song"), for screen readers.</summary>
    public string ActionName
    {
        get => (string)GetValue(ActionNameProperty);
        set => SetValue(ActionNameProperty, value);
    }

    private static HotkeyModifiers Modifiers()
    {
        var keys = ShortcutFocusPolicy.CurrentModifiers();
        var modifiers = HotkeyModifiers.None;
        if (keys.HasFlag(VirtualKeyModifiers.Control))
        {
            modifiers |= HotkeyModifiers.Control;
        }

        if (keys.HasFlag(VirtualKeyModifiers.Menu))
        {
            modifiers |= HotkeyModifiers.Alt;
        }

        if (keys.HasFlag(VirtualKeyModifiers.Shift))
        {
            modifiers |= HotkeyModifiers.Shift;
        }

        if (keys.HasFlag(VirtualKeyModifiers.Windows))
        {
            modifiers |= HotkeyModifiers.Windows;
        }

        return modifiers;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_keySource is null)
        {
            BeginCapture();
        }
        else
        {
            EndCapture();
        }
    }

    // Clicking elsewhere (or Tab) ends the recording without a change.
    private void OnLostFocus(object sender, RoutedEventArgs e) => EndCapture();

    private void BeginCapture()
    {
        if (XamlRoot?.Content is not UIElement root)
        {
            return;
        }

        _keySource = root;
        root.AddHandler(PreviewKeyDownEvent, _keyDown, handledEventsToo: true);
        root.AddHandler(PreviewKeyUpEvent, _keyUp, handledEventsToo: true);
        IsCapturing = true;
        Prompt("Press keys…", hint: false);
    }

    private void EndCapture()
    {
        if (_keySource is { } root)
        {
            root.RemoveHandler(PreviewKeyDownEvent, _keyDown);
            root.RemoveHandler(PreviewKeyUpEvent, _keyUp);
            _keySource = null;
        }

        IsCapturing = false;
        Show();
    }

    private void OnWindowPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = (int)e.Key;
        var modifiers = Modifiers();

        // Tab moves on as usual (and ends the recording through LostFocus).
        if (e.Key == VirtualKey.Tab && modifiers is HotkeyModifiers.None or HotkeyModifiers.Shift)
        {
            return;
        }

        e.Handled = true;
        if (e.Key == VirtualKey.Escape)
        {
            EndCapture();
            return;
        }

        if (e.Key == VirtualKey.Back)
        {
            Gesture = string.Empty;
            EndCapture();
            return;
        }

        if (HotkeyKeys.IsModifier(key))
        {
            // Show what is held so far: "Ctrl + Alt + …".
            Prompt(modifiers == HotkeyModifiers.None ? "Press keys…" : Spaced(HotkeyGesture.FormatModifiers(modifiers) + "+…"), hint: false);
            return;
        }

        var gesture = new HotkeyGesture(modifiers, key);
        if (gesture.IsValid)
        {
            Gesture = gesture.ToString();
            EndCapture();
        }
        else if (HotkeyKeys.NameOf(key) is null)
        {
            Prompt("That key can't be used", hint: true);
        }
        else
        {
            Prompt("Add Ctrl, Alt or Win", hint: true);
        }
    }

    // Key-ups stay here too: a Space release would click the focused button, an Alt release would open the window menu.
    private void OnWindowPreviewKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab)
        {
            e.Handled = true;
        }
    }

    // "Ctrl+Alt+Right" reads better as "Ctrl + Alt + Right" (a lone "+" key name doesn't occur: it is "=" or "NumPlus").
    private static string Spaced(string gesture) => gesture.Replace("+", " + ", StringComparison.Ordinal);

    private void Prompt(string text, bool hint)
    {
        Label.Text = text;
        VisualStateManager.GoToState(this, hint ? "CapturingHint" : "Capturing", false);
        AutomationProperties.SetName(Box, $"{ActionName}: {text}");
    }

    private void Show()
    {
        if (_keySource is not null)
        {
            return;
        }

        var none = string.IsNullOrWhiteSpace(Gesture);
        Label.Text = none ? "Not set" : Spaced(Gesture);
        VisualStateManager.GoToState(this, none ? "ShowingNone" : "Showing", false);
        AutomationProperties.SetName(Box, $"{ActionName}: {(none ? "no shortcut" : Gesture)}");
    }
}
