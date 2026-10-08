using Windows.UI;
using Windows.UI.ViewManagement;

namespace HushMusic.App.Services.Shell;

/// <summary>The Windows accent colour (Settings → Personalization → Colors) with the lighter and darker shades Windows derives from it.</summary>
public readonly record struct SystemAccentShades(
    Color Accent,
    Color Light1,
    Color Light2,
    Color Light3,
    Color Dark1,
    Color Dark2,
    Color Dark3);

/// <summary>Reads the Windows accent colour and reports when the user changes it. Thread-safe.</summary>
public static class SystemAccent
{
    private static readonly Lock Gate = new();
    private static UISettings? s_settings;
    private static EventHandler? s_changed;
    private static bool s_unavailable;

    /// <summary>
    /// Raised on a background thread when Windows' colours change (the accent, but also light/dark mode); compare
    /// <see cref="Read"/> if only the accent matters.
    /// </summary>
    public static event EventHandler? Changed
    {
        add
        {
            lock (Gate)
            {
                s_changed += value;
                _ = EnsureSettings();
            }
        }

        remove
        {
            lock (Gate)
            {
                s_changed -= value;
            }
        }
    }

    /// <summary>The current accent and its shades, or null when Windows doesn't report them.</summary>
    public static SystemAccentShades? Read()
    {
        UISettings? settings;
        lock (Gate)
        {
            settings = EnsureSettings();
        }

        if (settings is null)
        {
            return null;
        }

        try
        {
            return new SystemAccentShades(
                settings.GetColorValue(UIColorType.Accent),
                settings.GetColorValue(UIColorType.AccentLight1),
                settings.GetColorValue(UIColorType.AccentLight2),
                settings.GetColorValue(UIColorType.AccentLight3),
                settings.GetColorValue(UIColorType.AccentDark1),
                settings.GetColorValue(UIColorType.AccentDark2),
                settings.GetColorValue(UIColorType.AccentDark3));
        }
        catch (Exception)
        {
            return null;
        }
    }

    // One long-lived instance: UISettings only raises ColorValuesChanged while it is referenced. Caller holds Gate.
    private static UISettings? EnsureSettings()
    {
        if (s_settings is not null || s_unavailable)
        {
            return s_settings;
        }

        try
        {
            var settings = new UISettings();
            settings.ColorValuesChanged += (_, _) =>
            {
                EventHandler? handlers;
                lock (Gate)
                {
                    handlers = s_changed;
                }

                handlers?.Invoke(null, EventArgs.Empty);
            };
            s_settings = settings;
        }
        catch (Exception)
        {
            s_unavailable = true;
        }

        return s_settings;
    }
}
