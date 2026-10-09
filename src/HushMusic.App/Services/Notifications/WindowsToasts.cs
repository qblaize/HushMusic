using Microsoft.Win32;
using Velopack.Locators;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using HushMusic.App.Services.Windowing;

namespace HushMusic.App.Services.Notifications;

/// <summary>
/// Windows notifications (toasts) through the system API, Windows.UI.Notifications.
/// </summary>
/// <remarks>
/// Not the Windows App SDK's AppNotificationManager: it needs the Windows App Runtime's Singleton package, which a
/// self-contained install doesn't have (registering fails with "The specified module could not be found").
/// <para>
/// An unpackaged app shows toasts under an app ID Windows knows. An installed copy uses the one Velopack gives its Start
/// menu shortcut ("velopack.HushMusic.App"); <see cref="Register"/> also writes the ID's name and icon under
/// HKCU\Software\Classes\AppUserModelId, so a portable copy (no shortcut) and development builds work too. Clicks
/// arrive through each toast's Activated event, on a background thread, while the app runs.
/// </para>
/// </remarks>
internal sealed class WindowsToasts
{
    private const string DevelopmentAppId = "HushMusic.App.Development";
    private const string RegistrationKey = @"Software\Classes\AppUserModelId\";

    // Null in an MSIX build: the package identity names the app.
    private readonly string? _appId = Win32.IsPackaged() ? null : CurrentAppId();
    private readonly Lock _gate = new();

    // The toasts on screen, by group and tag: their Activated handlers only live as long as these objects.
    private readonly Dictionary<string, ToastNotification> _shown = [];
    private ToastNotifier? _notifier;

    public bool IsRegistered => _notifier is not null;

    /// <summary>
    /// Whether Windows lets this app notify (Settings → System → Notifications), or null when Windows can't say: an
    /// app ID that is only in the registry (a portable copy) has no entry there until its first toast.
    /// </summary>
    public NotificationSetting? Setting
    {
        get
        {
            try
            {
                return _notifier?.Setting;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>Makes the app ID known to Windows and opens the notifier. Throws when Windows refuses.</summary>
    public void Register()
    {
        lock (_gate)
        {
            if (_notifier is not null)
            {
                return;
            }

            if (_appId is not null)
            {
                WriteRegistration(_appId);
            }

            _notifier = _appId is null
                ? ToastNotificationManager.CreateToastNotifier()
                : ToastNotificationManager.CreateToastNotifier(_appId);
        }
    }

    /// <summary>
    /// Shows <paramref name="payload"/> (toast XML), replacing the toast with the same tag and group.
    /// <paramref name="activated"/> gets the clicked button's arguments, or the toast's launch arguments. Returns false
    /// when notifications are turned off for the app.
    /// </summary>
    public bool Show(string payload, string tag, string group, DateTimeOffset? expires, Action<string?> activated)
    {
        var notifier = _notifier ?? throw new InvalidOperationException("Call Register first.");
        if (Setting is { } setting && setting != NotificationSetting.Enabled)
        {
            return false;
        }

        var xml = new XmlDocument();
        xml.LoadXml(payload);
        var toast = new ToastNotification(xml) { Tag = tag, Group = group, ExpirationTime = expires };
        if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 18362))
        {
            toast.ExpiresOnReboot = true;
        }

        toast.Activated += (_, args) => activated((args as ToastActivatedEventArgs)?.Arguments);
        lock (_gate)
        {
            _shown[Key(tag, group)] = toast;
        }

        notifier.Show(toast);
        return true;
    }

    /// <summary>Removes a shown toast from the screen and Notification Center.</summary>
    public void Remove(string tag, string group)
    {
        lock (_gate)
        {
            _shown.Remove(Key(tag, group));
        }

        if (_notifier is null)
        {
            return;
        }

        if (_appId is null)
        {
            ToastNotificationManager.History.Remove(tag, group);
        }
        else
        {
            ToastNotificationManager.History.Remove(tag, group, _appId);
        }
    }

    /// <summary>Uninstall hook: removes this install's toasts and app ID registration. Never throws.</summary>
    public static void RemoveRegistration()
    {
        try
        {
            var appId = CurrentAppId();
            ToastNotificationManager.History.Clear(appId);
            Registry.CurrentUser.DeleteSubKeyTree(RegistrationKey + appId, throwOnMissingSubKey: false);
        }
        catch (Exception)
        {
            // Never fail the uninstall over this.
        }
    }

    // The Start menu shortcut's System.AppUserModel.ID, which Velopack sets to "velopack.<pack id>".
    private static string CurrentAppId()
    {
        try
        {
            if (VelopackLocator.IsCurrentSet && VelopackLocator.Current.AppId is { Length: > 0 } packId)
            {
                return "velopack." + packId;
            }
        }
        catch (Exception)
        {
            // Not installed by Velopack.
        }

        return DevelopmentAppId;
    }

    private static void WriteRegistration(string appId)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RegistrationKey + appId);
        key.SetValue("DisplayName", "Hush");
        key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "Assets", "Square44x44Logo.targetsize-48_altform-unplated.png"));
    }

    private static string Key(string tag, string group) => group + "/" + tag;
}
