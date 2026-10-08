using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace HushMusic.App.Services.Shell;

/// <summary>
/// Theme-aware resource lookups for code. The Application's own theme is always Dark, so
/// <c>Application.Current.Resources[key]</c> returns the dark value of a theme token even while the window shows Light.
/// Ask for a specific theme here instead (usually <see cref="IThemeService.ActualTheme"/> or an element's
/// <see cref="FrameworkElement.ActualTheme"/>). UI thread only.
/// </summary>
public static class ThemeResources
{
    private static readonly Dictionary<string, SolidColorBrush> TrackingByKey = [];
    private static ElementTheme s_theme = ElementTheme.Dark;

    /// <summary>The theme the window shows, as last applied by <see cref="ThemeService"/>.</summary>
    public static ElementTheme Current => s_theme;

    /// <summary>
    /// Looks <paramref name="key"/> up in the <paramref name="theme"/> dictionaries (Theme.xaml, then the stock Fluent
    /// ones), falling back to the theme-independent app resources (e.g. AccentBrush).
    /// </summary>
    public static bool TryGet(string key, ElementTheme theme, out object? value)
    {
        value = null;
        if (Application.Current?.Resources is not { } resources)
        {
            return false;
        }

        try
        {
            var themeKey = theme == ElementTheme.Light ? "Light" : "Dark";
            if (SearchThemes(resources, themeKey, key, out value)
                || (themeKey == "Dark" && SearchThemes(resources, "Default", key, out value)))
            {
                return true;
            }

            return resources.TryGetValue(key, out value) || TryIndexer(resources, key, out value);
        }
        catch (Exception)
        {
            value = null;
            return false;
        }
    }

    public static Color GetColor(string key, ElementTheme theme, Color fallback) =>
        TryGet(key, theme, out var value) && value is Color color ? color : fallback;

    public static T? Get<T>(string key, ElementTheme theme)
        where T : class =>
        TryGet(key, theme, out var value) ? value as T : null;

    /// <summary>
    /// A shared brush that always has the colour <paramref name="colorKey"/> (e.g. "TextPrimaryColor") has in the theme the
    /// window shows; recoloured in place on every theme change. For code and x:Bind functions that have no element to
    /// resolve a {ThemeResource} against. Note: it follows the window's theme, not a RequestedTheme="Dark" subtree.
    /// </summary>
    public static SolidColorBrush TrackingBrush(string colorKey)
    {
        if (!TrackingByKey.TryGetValue(colorKey, out var brush))
        {
            brush = new SolidColorBrush(GetColor(colorKey, s_theme, Colors.Transparent));
            TrackingByKey[colorKey] = brush;
        }

        return brush;
    }

    /// <summary>
    /// Every distinct value <paramref name="key"/> has in the app's theme dictionaries ("Default" and "Dark" count as
    /// Dark), with its theme. For theme-aware resources that code recolours in place, e.g. AccentTextBrush.
    /// </summary>
    public static IReadOnlyList<(ElementTheme Theme, object Value)> AllThemeValues(string key)
    {
        List<(ElementTheme, object)> found = [];
        if (Application.Current?.Resources is { } resources)
        {
            try
            {
                Collect(resources, key, found);
            }
            catch (Exception)
            {
                // A dictionary that can't be read contributes nothing.
            }
        }

        return found;
    }

    /// <summary>Called by <see cref="ThemeService"/> when the shown theme changes.</summary>
    internal static void Apply(ElementTheme theme)
    {
        s_theme = theme;
        foreach (var (colorKey, brush) in TrackingByKey)
        {
            brush.Color = GetColor(colorKey, theme, brush.Color);
        }
    }

    // Later merged dictionaries win, as in XAML lookup, so Theme.xaml (merged after XamlControlsResources) comes first.
    private static bool SearchThemes(ResourceDictionary dictionary, string themeKey, string key, out object? value)
    {
        if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed)
            && themed is ResourceDictionary theme
            && theme.TryGetValue(key, out value))
        {
            return true;
        }

        var merged = dictionary.MergedDictionaries;
        for (var i = merged.Count - 1; i >= 0; i--)
        {
            if (SearchThemes(merged[i], themeKey, key, out value))
            {
                return true;
            }
        }

        value = null;
        return false;
    }

    private static void Collect(ResourceDictionary dictionary, string key, List<(ElementTheme, object)> found)
    {
        foreach (var (themeKey, theme) in new[] { ("Default", ElementTheme.Dark), ("Dark", ElementTheme.Dark), ("Light", ElementTheme.Light) })
        {
            if (dictionary.ThemeDictionaries.TryGetValue(themeKey, out var themed)
                && themed is ResourceDictionary themeDictionary
                && themeDictionary.TryGetValue(key, out var value)
                && value is not null
                && !found.Exists(f => ReferenceEquals(f.Item2, value)))
            {
                found.Add((theme, value));
            }
        }

        foreach (var merged in dictionary.MergedDictionaries)
        {
            Collect(merged, key, found);
        }
    }

    // The indexer also searches merged dictionaries (TryGetValue doesn't); it throws for a missing key.
    private static bool TryIndexer(ResourceDictionary resources, string key, out object? value)
    {
        try
        {
            value = resources[key];
            return value is not null;
        }
        catch (Exception)
        {
            value = null;
            return false;
        }
    }
}
