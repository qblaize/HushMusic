using HushMusic.App.Services.Shell;
using HushMusic.Core.Models;

namespace HushMusic.App.Helpers;

/// <summary>Formatting helpers for x:Bind function bindings, e.g. <c>{x:Bind helpers:Format.Duration(Track.Duration)}</c>.</summary>
public static class Format
{
    /// <summary>Settings section header: uppercase eyebrow in the Hush design, sentence case in the Windows one.</summary>
    public static string SectionHeader(string title) =>
        DesignSystems.IsWindowsActive ? title : title.ToUpperInvariant();

    public static string Duration(TimeSpan? value)
    {
        if (value is not { } time)
        {
            return string.Empty;
        }

        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public static string Time(TimeSpan value) => Duration(value);

    public static string Artists(IReadOnlyList<ArtistRef>? artists) =>
        artists is null ? string.Empty : string.Join(", ", artists.Select(a => a.Name));

    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfNotEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAny(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public static bool Not(bool value) => !value;

    public static Visibility CollapsedIfAny(bool first, bool second) => first || second ? Visibility.Collapsed : Visibility.Visible;

    public static Visibility VisibleIfAndNot(bool value, bool unless) => value && !unless ? Visibility.Visible : Visibility.Collapsed;
}
