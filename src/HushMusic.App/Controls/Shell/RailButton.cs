using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Input;

namespace HushMusic.App.Controls.Shell;

/// <summary>
/// Sidebar rail item: an icon (or custom content) with a selected state. It never shows text itself; it asks its host
/// to show <see cref="Label"/> next to it right away on hover or keyboard focus (<see cref="LabelRequested"/>),
/// because the stock tooltip has a delay. Style: <c>RailButtonStyle</c>.
/// </summary>
public sealed partial class RailButton : Button
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut), typeof(string), typeof(RailButton), new PropertyMetadata(string.Empty, OnShortcutChanged));

    public static readonly DependencyProperty IsSelectedProperty = DependencyProperty.Register(
        nameof(IsSelected), typeof(bool), typeof(RailButton), new PropertyMetadata(false, OnIsSelectedChanged));

    private bool _labelShown;

    /// <summary>Show (true) or hide (false) the floating label for this button.</summary>
    public event EventHandler<bool>? LabelRequested;

    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    /// <summary>Optional keyboard shortcut shown dimmed after the label (e.g. "Ctrl+F").</summary>
    public string Shortcut
    {
        get => (string)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    public bool IsSelected
    {
        get => (bool)GetValue(IsSelectedProperty);
        set => SetValue(IsSelectedProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateSelectionState(useTransitions: false);
    }

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        ShowLabel(true);
    }

    protected override void OnPointerExited(PointerRoutedEventArgs e)
    {
        base.OnPointerExited(e);
        ShowLabel(FocusState == FocusState.Keyboard);
    }

    protected override void OnPointerCanceled(PointerRoutedEventArgs e)
    {
        base.OnPointerCanceled(e);
        ShowLabel(false);
    }

    protected override void OnPointerPressed(PointerRoutedEventArgs e)
    {
        base.OnPointerPressed(e);
        ShowLabel(false);
    }

    protected override void OnGotFocus(RoutedEventArgs e)
    {
        base.OnGotFocus(e);
        if (FocusState == FocusState.Keyboard)
        {
            ShowLabel(true);
        }
    }

    protected override void OnLostFocus(RoutedEventArgs e)
    {
        base.OnLostFocus(e);
        ShowLabel(false);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var button = (RailButton)d;
        AutomationProperties.SetName(button, e.NewValue as string ?? string.Empty);
        if (button._labelShown)
        {
            // Re-measure the visible label (e.g. the account name arrived while hovering).
            button.LabelRequested?.Invoke(button, true);
        }
    }

    private static void OnShortcutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        AutomationProperties.SetAcceleratorKey(d, e.NewValue as string ?? string.Empty);

    private static void OnIsSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((RailButton)d).UpdateSelectionState(useTransitions: true);

    private void UpdateSelectionState(bool useTransitions)
    {
        VisualStateManager.GoToState(this, IsSelected ? "Selected" : "Unselected", useTransitions);
        AutomationProperties.SetItemStatus(this, IsSelected ? "Current page" : string.Empty);
    }

    private void ShowLabel(bool show)
    {
        if (show == _labelShown && !show)
        {
            return;
        }

        _labelShown = show;
        LabelRequested?.Invoke(this, show);
    }
}
