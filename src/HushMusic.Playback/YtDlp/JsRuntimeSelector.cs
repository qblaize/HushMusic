using System.Text.RegularExpressions;

namespace HushMusic.Playback.YtDlp;

internal enum JsRuntimeSource
{
    /// <summary>The app's own Deno (tools\deno\&lt;version&gt;\deno.exe).</summary>
    ManagedDeno,

    /// <summary>Node.js found on PATH, used by "auto" until the managed Deno is ready.</summary>
    NodeOnPath,

    /// <summary>The user's explicit setting, passed through unchanged.</summary>
    Setting,

    /// <summary>Nothing usable yet: yt-dlp runs with its defaults (a Deno on PATH, if there is one).</summary>
    None,
}

/// <summary>The yt-dlp arguments that pick the JavaScript runtime for one run.</summary>
/// <param name="WantsManagedDeno">True when the managed Deno is the preferred runtime but is not installed yet.</param>
internal sealed record JsRuntimePlan(JsRuntimeSource Source, IReadOnlyList<string> Arguments, string Description, bool WantsManagedDeno);

/// <summary>
/// Chooses yt-dlp's JavaScript runtime from <see cref="Core.Abstractions.AppSettings.YtDlpJsRuntime"/> and what is installed:
/// <list type="bullet">
/// <item>"auto" (or empty): the managed Deno; until it is installed, Node.js from PATH; otherwise nothing yet (the caller
/// installs Deno and asks again).</item>
/// <item>"node" / "deno": that runtime from PATH, as before. When it is not there, the same as "auto": "node" was the
/// default before the app managed Deno, so a saved "node" on a PC without Node.js must not break playback.</item>
/// <item>Anything else ("node:C:\…\node.exe", "deno:C:\…\deno.exe", "quickjs", …) is passed to <c>--js-runtimes</c> unchanged.</item>
/// </list>
/// </summary>
internal static partial class JsRuntimeSelector
{
    public const string Auto = "auto";

    /// <summary>yt-dlp ignores older Node.js builds (EJS wiki and <c>NodeJsRuntime.MIN_SUPPORTED_VERSION</c>).</summary>
    public static readonly Version MinimumNodeVersion = new(22, 0, 0);

    /// <summary>The setting, trimmed, with empty meaning "auto".</summary>
    public static string Normalize(string? setting) => string.IsNullOrWhiteSpace(setting) ? Auto : setting.Trim();

    /// <summary>Whether choosing for <paramref name="setting"/> needs to know about a Node.js on PATH.</summary>
    public static bool UsesNodeOnPath(string? setting)
    {
        var value = Normalize(setting);
        return IsAuto(value) || IsBare(value, "node") || IsBare(value, "deno");
    }

    /// <summary>Whether choosing for <paramref name="setting"/> needs to know about a Deno on PATH.</summary>
    public static bool UsesDenoOnPath(string? setting) => IsBare(Normalize(setting), "deno");

    /// <param name="setting">The user's setting.</param>
    /// <param name="managedDeno">The installed managed deno.exe, or null.</param>
    /// <param name="nodeOnPath">A supported node.exe on PATH, or null.</param>
    /// <param name="denoOnPath">A deno.exe on PATH, or null (only looked for when the setting is "deno").</param>
    public static JsRuntimePlan Choose(string? setting, string? managedDeno, string? nodeOnPath, string? denoOnPath)
    {
        var value = Normalize(setting);
        if (!UsesNodeOnPath(value) || (IsBare(value, "node") && nodeOnPath is not null) || (IsBare(value, "deno") && denoOnPath is not null))
        {
            return new JsRuntimePlan(JsRuntimeSource.Setting, ["--js-runtimes", value], $"setting \"{value}\"", WantsManagedDeno: false);
        }

        // --no-js-runtimes first: yt-dlp enables "deno" by default and prefers it, so without it a Deno on PATH would
        // silently win over the Node.js chosen here.
        if (managedDeno is not null)
        {
            return new JsRuntimePlan(JsRuntimeSource.ManagedDeno, ["--no-js-runtimes", "--js-runtimes", "deno:" + managedDeno], $"managed Deno ({managedDeno})", WantsManagedDeno: false);
        }

        return nodeOnPath is not null
            ? new JsRuntimePlan(JsRuntimeSource.NodeOnPath, ["--no-js-runtimes", "--js-runtimes", "node:" + nodeOnPath], $"Node.js on PATH ({nodeOnPath})", WantsManagedDeno: true)
            : new JsRuntimePlan(JsRuntimeSource.None, [], "none (yt-dlp defaults)", WantsManagedDeno: true);
    }

    /// <summary>The first <paramref name="fileName"/> in the directories of a PATH value, like a command prompt finds it.</summary>
    public static string? FindOnPath(string fileName, string? pathVariable, Func<string, bool> fileExists)
    {
        foreach (var entry in (pathVariable ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var dir = entry.Trim('"');
            if (!Path.IsPathFullyQualified(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, fileName);
            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Parses <c>node --version</c> ("v22.11.0") or <c>deno --version</c> ("deno 2.9.7 (stable, …)").</summary>
    public static Version? ParseVersionOutput(string output)
    {
        var match = VersionRegex().Match(output);
        return match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : null;
    }

    private static bool IsAuto(string value) => value.Equals(Auto, StringComparison.OrdinalIgnoreCase);

    private static bool IsBare(string value, string runtime) => value.Equals(runtime, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\s*(?:v|deno\s+)?(\d+\.\d+\.\d+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex VersionRegex();
}
