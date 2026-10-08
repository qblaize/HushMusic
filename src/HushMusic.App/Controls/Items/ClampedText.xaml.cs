namespace HushMusic.App.Controls.Items;

/// <summary>Secondary text clamped to a few lines, with a "More" / "Less" text action when it doesn't fit.</summary>
public sealed partial class ClampedText : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(ClampedText), new PropertyMetadata(null, (d, _) => ((ClampedText)d).OnTextChanged()));

    public static readonly DependencyProperty CollapsedLinesProperty = DependencyProperty.Register(
        nameof(CollapsedLines), typeof(int), typeof(ClampedText), new PropertyMetadata(2, (d, _) => ((ClampedText)d).Apply()));

    private bool _expanded;

    public ClampedText()
    {
        InitializeComponent();
        Apply();
    }

    public string? Text
    {
        get => (string?)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int CollapsedLines
    {
        get => (int)GetValue(CollapsedLinesProperty);
        set => SetValue(CollapsedLinesProperty, value);
    }

    private void OnTextChanged()
    {
        _expanded = false;
        Visibility = string.IsNullOrWhiteSpace(Text) ? Visibility.Collapsed : Visibility.Visible;
        Apply();
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;
        Apply();
    }

    private void OnIsTextTrimmedChanged(TextBlock sender, IsTextTrimmedChangedEventArgs args) => UpdateToggle();

    private void Apply()
    {
        Body.MaxLines = _expanded ? 0 : CollapsedLines;
        ToggleButton.Content = _expanded ? "Less" : "More";
        UpdateToggle();
    }

    private void UpdateToggle() =>
        ToggleButton.Visibility = _expanded || Body.IsTextTrimmed ? Visibility.Visible : Visibility.Collapsed;
}
