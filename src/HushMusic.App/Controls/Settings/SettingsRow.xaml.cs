using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Markup;

namespace HushMusic.App.Controls.Settings;

/// <summary>
/// One row of a settings group: a label with an optional wrapping description on the left and the row's control
/// (<see cref="Action"/>, the XAML content) on the right. The control is named after the row for screen readers
/// unless the page gave it a name of its own.
/// </summary>
[ContentProperty(Name = nameof(Action))]
public sealed partial class SettingsRow : UserControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title), typeof(string), typeof(SettingsRow), new PropertyMetadata(string.Empty, OnLabelChanged));

    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description), typeof(string), typeof(SettingsRow), new PropertyMetadata(null, OnLabelChanged));

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(object), typeof(SettingsRow), new PropertyMetadata(null, OnLabelChanged));

    private string? _assignedName;

    public SettingsRow()
    {
        InitializeComponent();
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public string? Description
    {
        get => (string?)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    public object? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    private static void OnLabelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var row = (SettingsRow)d;
        if (row.Action is not UIElement control)
        {
            return;
        }

        var current = AutomationProperties.GetName(control);
        if (string.IsNullOrEmpty(current) || string.Equals(current, row._assignedName, StringComparison.Ordinal))
        {
            row._assignedName = row.Title;
            AutomationProperties.SetName(control, row.Title);
        }

        if (!string.IsNullOrEmpty(row.Description) && string.IsNullOrEmpty(AutomationProperties.GetHelpText(control)))
        {
            AutomationProperties.SetHelpText(control, row.Description);
        }
    }
}
