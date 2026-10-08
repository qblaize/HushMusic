namespace HushMusic.Core.Services;

/// <summary>A connected display.</summary>
/// <param name="DeviceName">The GDI device name, e.g. <c>\\.\DISPLAY2</c>.</param>
/// <param name="FriendlyName">The monitor's own name (e.g. "DELL U2720Q"), when Windows knows it.</param>
public sealed record DisplayInfo(string DeviceName, string? FriendlyName, bool IsPrimary);

/// <summary>
/// The "Taskbar player on" setting (<c>AppSettings.TaskbarWidgetDisplays</c>): "Primary", "All", or one display's device
/// name. Pure logic; the App finds the displays and their taskbars.
/// </summary>
public static class TaskbarDisplayChoice
{
    public const string Primary = "Primary";
    public const string All = "All";

    private const string DevicePrefix = @"\\.\DISPLAY";

    /// <summary>"Primary", "All" or a device name (as saved); anything unusable becomes "Primary".</summary>
    public static string Normalize(string? setting)
    {
        if (string.Equals(setting, All, StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        return string.IsNullOrWhiteSpace(setting) || string.Equals(setting, Primary, StringComparison.OrdinalIgnoreCase)
            ? Primary
            : setting.Trim();
    }

    /// <summary>
    /// The taskbars to put a player on, as display device names; null stands for the main taskbar. A chosen display that
    /// isn't connected falls back to the main taskbar, so undocking a laptop doesn't lose the player.
    /// </summary>
    public static IReadOnlyList<string?> Targets(string? setting, IReadOnlyList<DisplayInfo> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        var choice = Normalize(setting);
        if (choice == All)
        {
            return displays.Count == 0 ? [null] : [.. displays.Select(d => (string?)d.DeviceName)];
        }

        if (choice != Primary && displays.FirstOrDefault(d => SameDevice(d.DeviceName, choice)) is { } display)
        {
            return [display.DeviceName];
        }

        return [null];
    }

    /// <summary>"Display 2 — DELL U2720Q", or "Display 2" when the monitor has no name.</summary>
    public static string Label(DisplayInfo display)
    {
        ArgumentNullException.ThrowIfNull(display);
        var name = ShortName(display.DeviceName);
        return string.IsNullOrWhiteSpace(display.FriendlyName) ? name : $"{name} — {display.FriendlyName.Trim()}";
    }

    /// <summary>"Display 2" for <c>\\.\DISPLAY2</c>; other names are returned as they are.</summary>
    public static string ShortName(string deviceName)
    {
        ArgumentNullException.ThrowIfNull(deviceName);
        return deviceName.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(deviceName.AsSpan(DevicePrefix.Length), out var number)
            ? $"Display {number}"
            : deviceName;
    }

    public static bool SameDevice(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
