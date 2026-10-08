using Microsoft.Extensions.Logging;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Windows.UI;
using Windows.UI.ViewManagement;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Shell;

/// <summary>
/// Light / dark / follow-Windows. Applies <see cref="Core.Abstractions.AppSettings.Theme"/> to the window content
/// (<see cref="FrameworkElement.RequestedTheme"/>) and to the caption buttons, and re-applies when the setting or the
/// Windows app mode changes.
/// </summary>
public interface IThemeService
{
    /// <summary>The theme actually shown: Light or Dark (never Default). UI thread.</summary>
    ElementTheme ActualTheme { get; }

    /// <summary>Raised on the UI thread after <see cref="ActualTheme"/> changed.</summary>
    event EventHandler? ThemeChanged;

    /// <summary>Call once from the window constructor, after <see cref="Window.Content"/> is set.</summary>
    void AttachWindow(Window window);
}

public sealed class ThemeService(ISettingsService settings, IUiDispatcher dispatcher, ILogger<ThemeService> logger)
    : IThemeService, IDisposable
{
    private UISettings? _uiSettings;
    private Window? _window;

    public event EventHandler? ThemeChanged;

    public ElementTheme ActualTheme { get; private set; } = ElementTheme.Dark;

    public void AttachWindow(Window window)
    {
        if (_window is not null)
        {
            return;
        }

        _window = window;

        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Can't follow the Windows app mode; \"System\" theme falls back to dark");
        }

        settings.Changed += OnSettingsChanged;
        Apply(force: true);
    }

    public void Dispose()
    {
        settings.Changed -= OnSettingsChanged;
        if (_uiSettings is not null)
        {
            _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
        }
    }

    // Both arrive on background threads.
    private void OnSettingsChanged(object? sender, EventArgs e) => dispatcher.Run(() => Apply(force: false));

    private void OnColorValuesChanged(UISettings sender, object args) => dispatcher.Run(() => Apply(force: false));

    private void Apply(bool force)
    {
        if (_window is null)
        {
            return;
        }

        var theme = Resolve(settings.Current.Theme);
        if (!force && theme == ActualTheme)
        {
            return;
        }

        ActualTheme = theme;
        ThemeResources.Apply(theme);

        // Always whatever the window shows now: the content may be wrapped after construction.
        if (_window.Content is FrameworkElement content)
        {
            content.RequestedTheme = theme;
        }

        StyleCaptionButtons(_window.AppWindow.TitleBar, theme);
        logger.LogDebug("Theme {Theme} (setting {Setting})", theme, settings.Current.Theme);
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    private ElementTheme Resolve(string? setting)
    {
        if (string.Equals(setting, "Light", StringComparison.OrdinalIgnoreCase))
        {
            return ElementTheme.Light;
        }

        if (!string.Equals(setting, "System", StringComparison.OrdinalIgnoreCase) || _uiSettings is null)
        {
            return ElementTheme.Dark;
        }

        // Windows "app mode": the app background colour is black in dark mode and white in light mode.
        try
        {
            var background = _uiSettings.GetColorValue(UIColorType.Background);
            return background.R + background.G + background.B > 3 * 128 ? ElementTheme.Light : ElementTheme.Dark;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not read the Windows app mode");
            return ElementTheme.Dark;
        }
    }

    // Caption buttons blend into the chrome: no fill, secondary glyphs, a faint hover fill.
    private static void StyleCaptionButtons(AppWindowTitleBar titleBar, ElementTheme theme)
    {
        Color Get(string key, Color fallback) => ThemeResources.GetColor(key, theme, fallback);
        var light = theme == ElementTheme.Light;

        titleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        titleBar.PreferredTheme = light ? TitleBarTheme.Light : TitleBarTheme.Dark;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonForegroundColor = Get("TextSecondaryColor", ColorHelper.FromArgb(0xFF, 0xA1, 0xA1, 0xA6));
        titleBar.ButtonInactiveForegroundColor = Get("TextTertiaryColor", ColorHelper.FromArgb(0xFF, 0x6E, 0x6E, 0x73));

        // Hover/pressed colours may not honour alpha, so pre-blend the translucent fills over the window background.
        var background = Get("AppBackgroundColor", light ? Colors.White : ColorHelper.FromArgb(0xFF, 0x0B, 0x0B, 0x0C));
        titleBar.ButtonHoverBackgroundColor = Over(Get("HoverFillColor", ColorHelper.FromArgb(0x0F, 0x80, 0x80, 0x80)), background);
        titleBar.ButtonHoverForegroundColor = Get("TextPrimaryColor", light ? Colors.Black : Colors.White);
        titleBar.ButtonPressedBackgroundColor = Over(Get("PressedFillColor", ColorHelper.FromArgb(0x08, 0x80, 0x80, 0x80)), background);
        titleBar.ButtonPressedForegroundColor = Get("TextSecondaryColor", ColorHelper.FromArgb(0xFF, 0x80, 0x80, 0x80));
    }

    private static Color Over(Color fill, Color background)
    {
        var a = fill.A / 255.0;
        byte Mix(byte f, byte b) => (byte)Math.Round((f * a) + (b * (1 - a)));
        return ColorHelper.FromArgb(0xFF, Mix(fill.R, background.R), Mix(fill.G, background.G), Mix(fill.B, background.B));
    }
}
