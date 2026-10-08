using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using HushMusic.App.Controls.Items;

namespace HushMusic.App.Controls.Settings;

/// <summary>
/// Apple-style segmented control for one choice out of a few (e.g. Light | Dark | Auto). Two-way bind
/// <see cref="SelectedValue"/>. Built from ToggleButtons kept exclusive by <see cref="ToggleGroup"/>, because
/// custom-templated RadioButtons crash during layout.
/// </summary>
public sealed partial class SegmentedPicker : UserControl
{
    public static readonly DependencyProperty OptionsProperty = DependencyProperty.Register(
        nameof(Options), typeof(IReadOnlyList<SettingsChoice>), typeof(SegmentedPicker), new PropertyMetadata(null, OnOptionsChanged));

    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(string), typeof(SegmentedPicker), new PropertyMetadata(null, OnSelectedValueChanged));

    private bool _syncing;

    public SegmentedPicker()
    {
        InitializeComponent();
    }

    public IReadOnlyList<SettingsChoice>? Options
    {
        get => (IReadOnlyList<SettingsChoice>?)GetValue(OptionsProperty);
        set => SetValue(OptionsProperty, value);
    }

    public string? SelectedValue
    {
        get => (string?)GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    private static void OnOptionsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SegmentedPicker)d).Rebuild();

    private static void OnSelectedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((SegmentedPicker)d).Sync();

    private void Rebuild()
    {
        foreach (var old in Segments.Children.OfType<ToggleButton>())
        {
            old.Checked -= OnSegmentChecked;
            old.Unchecked -= OnSegmentUnchecked;
        }

        Segments.Children.Clear();
        var options = Options ?? [];
        var style = (Style)Application.Current.Resources["SettingsSegmentButtonStyle"];
        for (var i = 0; i < options.Count; i++)
        {
            var option = options[i];
            var segment = new ToggleButton { Content = option.Label, Tag = option.Value, Style = style };
            AutomationProperties.SetName(segment, option.Label);
            AutomationProperties.SetPositionInSet(segment, i + 1);
            AutomationProperties.SetSizeOfSet(segment, options.Count);
            segment.Checked += OnSegmentChecked;
            segment.Unchecked += OnSegmentUnchecked;
            Segments.Children.Add(segment);
        }

        Sync();
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var segment in Segments.Children.OfType<ToggleButton>())
            {
                segment.IsChecked = string.Equals(segment.Tag as string, SelectedValue, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSegmentChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not ToggleButton { Tag: string value } segment)
        {
            return;
        }

        ToggleGroup.OnChecked(segment);
        SelectedValue = value;
    }

    private void OnSegmentUnchecked(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            ToggleGroup.OnUnchecked((ToggleButton)sender);
        }
    }
}
