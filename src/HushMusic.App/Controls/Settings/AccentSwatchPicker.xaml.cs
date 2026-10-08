using Microsoft.UI;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using HushMusic.App.Controls.Items;
using HushMusic.App.Services.Shell;

namespace HushMusic.App.Controls.Settings;

/// <summary>
/// A row of circular accent swatches; the selected one gets a ring. Two-way bind <see cref="SelectedValue"/>.
/// When <see cref="Swatches"/> is replaced with the same values (e.g. recoloured for the other theme) the existing
/// buttons are recoloured in place, so keyboard focus survives a theme switch.
/// </summary>
public sealed partial class AccentSwatchPicker : UserControl
{
    public static readonly DependencyProperty SwatchesProperty = DependencyProperty.Register(
        nameof(Swatches), typeof(IReadOnlyList<AccentSwatch>), typeof(AccentSwatchPicker), new PropertyMetadata(null, OnSwatchesChanged));

    public static readonly DependencyProperty SelectedValueProperty = DependencyProperty.Register(
        nameof(SelectedValue), typeof(string), typeof(AccentSwatchPicker), new PropertyMetadata(null, OnSelectedValueChanged));

    private bool _syncing;

    public AccentSwatchPicker()
    {
        InitializeComponent();
    }

    public IReadOnlyList<AccentSwatch>? Swatches
    {
        get => (IReadOnlyList<AccentSwatch>?)GetValue(SwatchesProperty);
        set => SetValue(SwatchesProperty, value);
    }

    public string? SelectedValue
    {
        get => (string?)GetValue(SelectedValueProperty);
        set => SetValue(SelectedValueProperty, value);
    }

    private static void OnSwatchesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AccentSwatchPicker)d).Update();

    private static void OnSelectedValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AccentSwatchPicker)d).Sync();

    private static Brush Fill(IReadOnlyList<Color> colors)
    {
        if (colors.Count == 1)
        {
            return new SolidColorBrush(colors[0]);
        }

        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        for (var i = 0; i < colors.Count; i++)
        {
            gradient.GradientStops.Add(new GradientStop { Color = colors[i], Offset = (double)i / (colors.Count - 1) });
        }

        return gradient;
    }

    private static Color Average(IReadOnlyList<Color> colors) => ColorHelper.FromArgb(
        0xFF,
        (byte)colors.Average(c => c.R),
        (byte)colors.Average(c => c.G),
        (byte)colors.Average(c => c.B));

    private void Update()
    {
        var swatches = Swatches ?? [];
        var buttons = SwatchPanel.Children.OfType<ToggleButton>().ToList();
        if (buttons.Count != swatches.Count || buttons.Where((b, i) => b.Tag as string != swatches[i].Value).Any())
        {
            Rebuild(swatches);
            return;
        }

        for (var i = 0; i < swatches.Count; i++)
        {
            Paint(buttons[i], swatches[i]);
        }
    }

    private void Rebuild(IReadOnlyList<AccentSwatch> swatches)
    {
        foreach (var old in SwatchPanel.Children.OfType<ToggleButton>())
        {
            old.Checked -= OnSwatchChecked;
            old.Unchecked -= OnSwatchUnchecked;
        }

        SwatchPanel.Children.Clear();
        var style = (Style)Application.Current.Resources["SettingsSwatchToggleStyle"];
        for (var i = 0; i < swatches.Count; i++)
        {
            var swatch = swatches[i];
            var button = new ToggleButton { Tag = swatch.Value, Style = style };
            if (swatch.IsArtwork)
            {
                button.Content = new FontIcon { Glyph = "", FontSize = 11 };
            }

            var label = string.IsNullOrEmpty(swatch.Description) ? swatch.Name : $"{swatch.Name}, {swatch.Description}";
            AutomationProperties.SetName(button, label);
            AutomationProperties.SetPositionInSet(button, i + 1);
            AutomationProperties.SetSizeOfSet(button, swatches.Count);
            ToolTipService.SetToolTip(button, label);
            button.Checked += OnSwatchChecked;
            button.Unchecked += OnSwatchUnchecked;
            Paint(button, swatch);
            SwatchPanel.Children.Add(button);
        }

        Sync();
    }

    private static void Paint(ToggleButton button, AccentSwatch swatch)
    {
        if (swatch.Colors.Count == 0)
        {
            return;
        }

        var fill = Fill(swatch.Colors);
        button.Background = fill;
        button.BorderBrush = fill;
        button.Foreground = new SolidColorBrush(AccentPalette.ForegroundFor(Average(swatch.Colors)));
    }

    private void Sync()
    {
        _syncing = true;
        try
        {
            var buttons = SwatchPanel.Children.OfType<ToggleButton>().ToList();
            // An unknown stored value shows the first swatch (album art) as selected, matching what the app does.
            var match = buttons.FirstOrDefault(b => string.Equals(b.Tag as string, SelectedValue, StringComparison.OrdinalIgnoreCase))
                ?? buttons.FirstOrDefault();
            foreach (var button in buttons)
            {
                button.IsChecked = ReferenceEquals(button, match);
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnSwatchChecked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not ToggleButton { Tag: string value } button)
        {
            return;
        }

        ToggleGroup.OnChecked(button);
        SelectedValue = value;
    }

    private void OnSwatchUnchecked(object sender, RoutedEventArgs e)
    {
        if (!_syncing)
        {
            ToggleGroup.OnUnchecked((ToggleButton)sender);
        }
    }
}
