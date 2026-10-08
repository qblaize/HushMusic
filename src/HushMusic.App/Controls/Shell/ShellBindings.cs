using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;
using HushMusic.App.Helpers;
using HushMusic.App.Services.Shell;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Controls.Shell;

/// <summary>x:Bind function helpers for the shell controls, e.g. <c>Foreground="{x:Bind shell:ShellBindings.AccentIf(ViewModel.IsCurrent), Mode=OneWay}"</c>.</summary>
/// <remarks>
/// The palette brushes returned here follow the theme the window shows (<see cref="ThemeResources.TrackingBrush"/>),
/// recoloured in place on a theme change. Inside a RequestedTheme="Dark" subtree use {ThemeResource} in XAML instead.
/// </remarks>
public static class ShellBindings
{
    private static Brush? s_accent;

    // AccentBrush is one shared instance whose Color changes, so resolving it once is fine.
    private static Brush Accent => s_accent ??= ThemeResources.Get<Brush>("AccentBrush", ElementTheme.Dark) ?? new SolidColorBrush(Colors.White);

    public static Brush AccentIf(bool active) => active ? Accent : ThemeResources.TrackingBrush("TextPrimaryColor");

    public static Brush AccentOrSecondary(bool active) => active ? Accent : ThemeResources.TrackingBrush("TextSecondaryColor");

    public static FontWeight SemiBoldIf(bool active) => active ? FontWeights.SemiBold : FontWeights.Normal;

    public static CornerRadius ArtCorners(bool round, double size) =>
        round ? new CornerRadius(size / 2) : new CornerRadius(Math.Max(4, Math.Round(size / 7)));

    /// <summary>Rail selection: true when <paramref name="current"/> is the page named <paramref name="key"/>.</summary>
    public static bool IsPage(PageKey? current, string key) =>
        current is { } page && string.Equals(page.ToString(), key, StringComparison.Ordinal);

    public static Brush SeverityBrush(NotificationSeverity severity) => ThemeResources.TrackingBrush(severity switch
    {
        NotificationSeverity.Error => "DangerColor",
        NotificationSeverity.Warning => "WarningColor",
        NotificationSeverity.Success => "SuccessColor",
        _ => "TextSecondaryColor",
    });
}

/// <summary>Formats a slider value in seconds as m:ss for the seek thumb tooltip.</summary>
public sealed partial class SecondsToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is double seconds ? Format.Time(TimeSpan.FromSeconds(Math.Max(0, seconds))) : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
