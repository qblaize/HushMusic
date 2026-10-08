using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Windows.AppLifecycle;
using Windows.ApplicationModel.Core;

namespace HushMusic.App.Services.Shell;

/// <summary>
/// The two looks of the app: Hush's own design, or the stock Windows 11 (Fluent) one. The choice is stored in
/// <see cref="Core.Abstractions.AppSettings.DesignSystem"/> and applied once at startup, before the window is created.
/// </summary>
public static class DesignSystems
{
    public const string Hush = "Hush";

    public const string Windows = "Windows";

    /// <summary>A restarted copy gets <c>--restart-after &lt;process id&gt;</c> and waits for that process to exit.</summary>
    public const string RestartArgument = "--restart-after";

    private static readonly TimeSpan RestartWait = TimeSpan.FromSeconds(15);

    // Each Hush dictionary merged in App.xaml, and the Windows design's dictionary that redefines its keys.
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-appx:///Resources/Theme.xaml"] = "ms-appx:///Resources/Fluent/Theme.xaml",
        ["ms-appx:///Resources/Styles.xaml"] = "ms-appx:///Resources/Fluent/Styles.xaml",
        ["ms-appx:///Resources/ShellResources.xaml"] = "ms-appx:///Resources/Fluent/ShellResources.xaml",
        ["ms-appx:///Resources/PageResources.xaml"] = "ms-appx:///Resources/Fluent/PageResources.xaml",
    };

    // Theme.xaml gives stock controls Hush's violet. These are system resources, not entries of the stock dictionary.
    private static readonly HashSet<string> SystemAccentKeys =
    [
        "SystemAccentColor",
        "SystemAccentColorLight1",
        "SystemAccentColorLight2",
        "SystemAccentColorLight3",
        "SystemAccentColorDark1",
        "SystemAccentColorDark2",
        "SystemAccentColorDark3",
    ];

    /// <summary>The design the running app was started with.</summary>
    public static string Active { get; set; } = Hush;

    public static bool IsWindowsActive => IsWindows(Active);

    public static bool IsWindows(string? value) => string.Equals(value, Windows, StringComparison.OrdinalIgnoreCase);

    public static string Normalize(string? value) => IsWindows(value) ? Windows : Hush;

    /// <summary>
    /// Puts the Windows design over the Hush dictionaries merged in App.xaml. Call once, before any window or page exists.
    /// Returns how many stock overrides were dropped from the Hush theme.
    /// </summary>
    /// <remarks>
    /// Resources/Fluent has one dictionary per Hush dictionary, redefining its keys with Fluent values. Each is merged right
    /// after the dictionary it overrides, and the Hush dictionaries after that are loaded again, so their StaticResource
    /// references (BasedOn styles, setter values) find the overrides whether XAML resolves them on load or on first use.
    /// The Hush theme also restyles stock keys (Fluent brushes, the type ramp, corner radii, the accent): those entries are
    /// dropped, so stock controls get their own values and the Windows accent colour.
    /// </remarks>
    public static int UseWindowsResources(ResourceDictionary resources)
    {
        var merged = resources.MergedDictionaries;
        var first = -1;
        for (var i = 0; i < merged.Count && first < 0; i++)
        {
            if (Overrides.ContainsKey(SourceOf(merged[i])))
            {
                first = i;
            }
        }

        if (first < 0)
        {
            return 0;
        }

        var original = merged.ToList();
        var dropped = DropStockOverrides(merged[first], original.Take(first).ToList());
        try
        {
            while (merged.Count > first + 1)
            {
                merged.RemoveAt(merged.Count - 1);
            }

            merged.Add(Load(Overrides[SourceOf(original[first])]));
            foreach (var dictionary in original.Skip(first + 1))
            {
                var source = SourceOf(dictionary);
                merged.Add(source.Length > 0 ? Load(source) : dictionary);
                if (Overrides.TryGetValue(source, out var windows))
                {
                    merged.Add(Load(windows));
                }
            }
        }
        catch
        {
            // Back to the dictionaries App.xaml loaded, so the app still starts in the Hush design (stock controls stay stock).
            merged.Clear();
            original.ForEach(merged.Add);
            throw;
        }

        return dropped;
    }

    /// <summary>
    /// Restarts the app (a design change needs a fresh start): starts a new copy that waits for this one to exit, then
    /// quits through the normal shutdown, which saves the queue and stops the features. Only returns when it failed.
    /// </summary>
    public static AppRestartFailureReason Restart()
    {
        if (Environment.ProcessPath is not { } executable || App.MainWindow is not { } window)
        {
            return AppRestartFailureReason.Other;
        }

        try
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = AppContext.BaseDirectory };
            start.ArgumentList.Add(RestartArgument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            using var replacement = Process.Start(start);
            if (replacement is null)
            {
                return AppRestartFailureReason.Other;
            }

            // The user just clicked in this window, so the new copy may take the foreground when its window opens.
            AllowSetForegroundWindow((uint)replacement.Id);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return AppRestartFailureReason.Other;
        }

        // Give up the single-instance key now: the new copy must never hand its launch to this one while it shuts down.
        AppInstance.GetCurrent().UnregisterKey();
        window.Modes.Quit();
        Environment.Exit(0);
        return AppRestartFailureReason.Other;
    }

    /// <summary>
    /// In a copy started by <see cref="Restart"/>: waits (up to 15 s) for the previous copy to finish its shutdown, which
    /// saves the queue this copy restores, so the two never run at once. Call before the single-instance check.
    /// </summary>
    public static void WaitForPreviousInstance(IReadOnlyList<string> arguments)
    {
        var index = -1;
        for (var i = 0; i < arguments.Count - 1 && index < 0; i++)
        {
            if (string.Equals(arguments[i], RestartArgument, StringComparison.OrdinalIgnoreCase))
            {
                index = i;
            }
        }

        if (index < 0 || !int.TryParse(arguments[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
        {
            return;
        }

        try
        {
            using var previous = Process.GetProcessById(processId);
            using var current = Process.GetCurrentProcess();

            // A process started after this one only reuses the id; the copy that restarted us is older.
            if (previous.StartTime <= current.StartTime)
            {
                previous.WaitForExit(RestartWait);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone (or not ours to inspect).
        }
    }

    private static int DropStockOverrides(ResourceDictionary theme, IReadOnlyList<ResourceDictionary> stock)
    {
        var dropped = 0;
        var dictionaries = theme.ThemeDictionaries.Values.OfType<ResourceDictionary>().Prepend(theme);
        foreach (var dictionary in dictionaries)
        {
            foreach (var key in dictionary.Keys.ToList())
            {
                if ((key is string name && SystemAccentKeys.Contains(name)) || stock.Any(s => Defines(s, key)))
                {
                    dictionary.Remove(key);
                    dropped++;
                }
            }
        }

        return dropped;
    }

    private static bool Defines(ResourceDictionary dictionary, object key) =>
        dictionary.ContainsKey(key)
        || dictionary.ThemeDictionaries.Values.OfType<ResourceDictionary>().Any(theme => theme.ContainsKey(key))
        || dictionary.MergedDictionaries.Any(merged => Defines(merged, key));

    private static ResourceDictionary Load(string source) => new() { Source = new Uri(source) };

    private static string SourceOf(ResourceDictionary dictionary) => dictionary.Source?.OriginalString ?? string.Empty;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AllowSetForegroundWindow(uint processId);
}

/// <summary>Values of <see cref="Core.Abstractions.AppSettings.PlayerLayout"/>.</summary>
public static class PlayerLayouts
{
    public const string Standard = "Standard";

    public const string Minimal = "Minimal";

    public static bool IsMinimal(string? value) => string.Equals(value, Minimal, StringComparison.OrdinalIgnoreCase);
}
