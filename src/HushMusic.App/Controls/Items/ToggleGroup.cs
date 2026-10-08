using Microsoft.UI.Xaml.Controls.Primitives;

namespace HushMusic.App.Controls.Items;

/// <summary>
/// Keeps the ToggleButtons of one panel (chips, segmented tabs) mutually exclusive with exactly one checked.
/// Driven from Checked/Unchecked, so mouse, keyboard, UI Automation and code all behave the same.
/// </summary>
internal static class ToggleGroup
{
    /// <summary>Call from a toggle's Checked handler: unchecks its siblings.</summary>
    public static void OnChecked(ToggleButton toggle)
    {
        foreach (var sibling in Siblings(toggle))
        {
            sibling.IsChecked = false;
        }
    }

    /// <summary>Call from a toggle's Unchecked handler: clicking the selected toggle keeps it selected.</summary>
    public static void OnUnchecked(ToggleButton toggle)
    {
        if (!Siblings(toggle).Any(t => t.IsChecked == true))
        {
            toggle.IsChecked = true;
        }
    }

    /// <summary>Checks the toggle whose Tag is <paramref name="tag"/> (its Checked handler clears the others).</summary>
    public static void CheckTag(Panel group, string tag)
    {
        if (group.Children.OfType<ToggleButton>().FirstOrDefault(t => t.Tag as string == tag) is { IsChecked: not true } match)
        {
            match.IsChecked = true;
        }
    }

    private static IEnumerable<ToggleButton> Siblings(ToggleButton toggle) =>
        (toggle.Parent as Panel)?.Children.OfType<ToggleButton>().Where(t => !ReferenceEquals(t, toggle)) ?? [];
}
