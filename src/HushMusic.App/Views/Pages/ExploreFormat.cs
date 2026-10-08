using Microsoft.UI.Xaml.Media;
using Windows.UI;
using HushMusic.App.Selectors;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Models;

namespace HushMusic.App.Views.Pages;

/// <summary>x:Bind function helpers for the Explore pages.</summary>
public static class ExploreFormat
{
    // How strongly a category colour washes over its tile; low enough for primary text on both themes.
    private const byte WashAlpha = 0x38;

    private static readonly Dictionary<uint, SolidColorBrush> s_brushes = [];
    private static SolidColorBrush? s_transparent;
    private static MediaItemTemplateSelector? s_wideCards;

    /// <summary>
    /// Card templates for a grid. A grid of music videos uses 16:9 cards throughout, also for the odd video YouTube Music
    /// files as an episode, so the rows stay even.
    /// </summary>
    public static DataTemplateSelector CardTemplates(bool videos)
    {
        var resources = Application.Current.Resources;
        if (!videos)
        {
            return (DataTemplateSelector)resources["CardTemplateSelector"];
        }

        var wide = (DataTemplate)resources["WideCardTemplate"];
        return s_wideCards ??= new MediaItemTemplateSelector { TrackTemplate = wide, VideoTemplate = wide };
    }

    public static string Title(MoodCategory? category) => category?.Title ?? string.Empty;

    /// <summary>The category colour, faint, as a solid fill over the tile surface.</summary>
    public static Brush MoodWash(MoodCategory? category) =>
        category?.Color is { } argb ? BrushFor((argb & 0x00FFFFFFu) | ((uint)WashAlpha << 24)) : s_transparent ??= new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));

    /// <summary>The category colour at full strength for the tile's edge; the accent when YouTube Music sends none.</summary>
    public static Brush MoodStripe(MoodCategory? category) =>
        category?.Color is { } argb ? BrushFor(argb | 0xFF000000u) : (Brush)Application.Current.Resources["AccentBrush"];

    public static string TrendGlyph(ChartTrend? trend) => trend switch
    {
        ChartTrend.Up => "",
        ChartTrend.Down => "",
        ChartTrend.Neutral => "",
        _ => string.Empty,
    };

    public static Brush TrendBrush(ChartTrend? trend) => ThemeResources.TrackingBrush(trend switch
    {
        ChartTrend.Up => "SuccessColor",
        ChartTrend.Down => "DangerColor",
        _ => "TextTertiaryColor",
    });

    public static Visibility VisibleIfTrend(ChartTrend? trend) => trend is null ? Visibility.Collapsed : Visibility.Visible;

    private static SolidColorBrush BrushFor(uint argb)
    {
        if (!s_brushes.TryGetValue(argb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            s_brushes[argb] = brush;
        }

        return brush;
    }
}
