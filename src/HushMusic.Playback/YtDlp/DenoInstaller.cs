using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.Core;

namespace HushMusic.Playback.YtDlp;

/// <summary>An app-managed Deno build: its version (also its folder name) and executable.</summary>
internal sealed record InstalledDeno(Version Version, string ExecutablePath);

/// <summary>
/// Installs the official Deno build (a zip holding just deno.exe) into <c>tools\deno\&lt;version&gt;\</c>, verified against
/// the release's <c>.sha256sum</c> file. yt-dlp needs a JavaScript runtime to solve YouTube's playback challenges, and
/// Deno is the one it recommends (it runs the solver without file system or network access).
/// </summary>
internal static class DenoInstaller
{
    private const string ReleasesBase = "https://github.com/denoland/deno/releases/";
    private const string ExecutableName = "deno.exe";
    private const string LastCheckFileName = "last-update-check.txt";

    /// <summary>yt-dlp ignores older Deno builds (EJS wiki and <c>DenoJsRuntime.MIN_SUPPORTED_VERSION</c>).</summary>
    public static readonly Version MinimumVersion = new(2, 3, 0);

    /// <summary>How often the managed Deno is checked for updates (it changes far less often than yt-dlp needs to).</summary>
    public static readonly TimeSpan UpdateInterval = TimeSpan.FromDays(7);

    /// <summary>The release asset for this process, or null where Deno has no Windows build (x86).</summary>
    public static string? AssetName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.X64 => "deno-x86_64-pc-windows-msvc.zip",
        Architecture.Arm64 => "deno-aarch64-pc-windows-msvc.zip",
        _ => null,
    };

    /// <summary>The newest supported version installed under <paramref name="root"/>, or null when there is none.</summary>
    public static InstalledDeno? FindInstalled(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .Select(dir => (Name: Path.GetFileName(dir), Exe: Path.Combine(dir, ExecutableName)))
            .Select(d => Version.TryParse(d.Name, out var version) && version >= MinimumVersion && File.Exists(d.Exe)
                ? new InstalledDeno(version, d.Exe)
                : null)
            .OfType<InstalledDeno>()
            .OrderByDescending(d => d.Version)
            .FirstOrDefault();
    }

    /// <summary>The version of the latest stable release.</summary>
    public static async Task<Version> GetLatestVersionAsync(CancellationToken cancellationToken)
    {
        var (tag, status) = await ManagedTools.GetLatestTagAsync(ReleasesBase, cancellationToken).ConfigureAwait(false);
        return tag is not null && TryParseTag(tag, out var version)
            ? version
            : throw new HushException($"Could not find the latest Deno release (HTTP {(int)status}).");
    }

    /// <summary>True when <paramref name="candidate"/> is newer than <paramref name="installed"/> (or nothing is installed).</summary>
    public static bool IsNewer(Version candidate, Version? installed) => installed is null || candidate > installed;

    /// <summary>Downloads, verifies and extracts <paramref name="version"/>. Returns the path of its deno.exe.</summary>
    public static async Task<string> InstallAsync(string root, Version version, ILogger logger, CancellationToken cancellationToken)
    {
        var asset = AssetName ?? throw new HushException("Deno has no Windows build for this processor.");
        if (version < MinimumVersion)
        {
            throw new HushException($"Deno {version} is older than {MinimumVersion}, the oldest version yt-dlp supports.");
        }

        Directory.CreateDirectory(root);
        var folder = version.ToString();
        var target = Path.Combine(root, folder);
        var exe = Path.Combine(target, ExecutableName);
        if (File.Exists(exe))
        {
            return exe;
        }

        using var http = ManagedTools.CreateClient(allowRedirect: true);
        var releaseBase = $"{ReleasesBase}download/v{folder}/";
        var checksumFile = await http.GetStringAsync(releaseBase + asset + ".sha256sum", cancellationToken).ConfigureAwait(false);
        var expected = ParseChecksum(checksumFile, asset)
            ?? throw new HushException($"The Deno {folder} release has no usable checksum for {asset}.");

        var hash = await ManagedTools.InstallZipAsync(http, releaseBase + asset, expected, root, folder, ExecutableName, logger, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("Installed Deno {Version} to {Path} (SHA-256 {Hash})", folder, target, hash);
        return exe;
    }

    /// <summary>Removes every version except <paramref name="keep"/>; folders in use by a running Deno are retried next time.</summary>
    public static void RemoveOtherVersions(string root, Version keep, ILogger logger) =>
        ManagedTools.RemoveOtherVersions(root, keep.ToString(), logger);

    /// <summary>When the managed Deno was last checked for updates (null if never, or unreadable).</summary>
    public static DateTimeOffset? ReadLastCheck(string root)
    {
        try
        {
            var text = File.ReadAllText(Path.Combine(root, LastCheckFileName)).Trim();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteLastCheck(string root, DateTimeOffset when)
    {
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, LastCheckFileName), when.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>True when the last check is older than <see cref="UpdateInterval"/>, never happened, or lies in the future.</summary>
    internal static bool IsUpdateCheckDue(DateTimeOffset? lastCheck, DateTimeOffset now) =>
        lastCheck is not { } last || last > now || now - last >= UpdateInterval;

    /// <summary>Release tags look like "v2.9.7". Pre-releases ("v2.0.0-rc.1") are rejected.</summary>
    internal static bool TryParseTag(string tag, [NotNullWhen(true)] out Version? version)
    {
        if (tag.StartsWith('v') && Version.TryParse(tag.AsSpan(1), out var parsed) && parsed.Build >= 0 && parsed.Revision < 0)
        {
            version = parsed;
            return true;
        }

        version = null;
        return false;
    }

    /// <summary>
    /// Finds the SHA-256 of <paramref name="assetName"/> in a release's checksum file. Deno publishes its Windows checksums
    /// as PowerShell <c>Get-FileHash | Format-List</c> output ("Algorithm : SHA256", "Hash : &lt;HEX&gt;", "Path : …\asset"),
    /// and the others in sha256sum format ("&lt;hex&gt;  &lt;file name&gt;"); both are accepted. Returns lowercase hex, or null.
    /// </summary>
    internal static string? ParseChecksum(string text, string assetName)
    {
        string? listHash = null;
        string? listPath = null;
        string? listAlgorithm = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('﻿');
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (IsSha256(parts[0]))
            {
                if (parts.Length == 1 || FileName(parts[1].TrimStart('*')) == assetName)
                {
                    return parts[0].ToLowerInvariant();
                }

                continue;
            }

            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                continue;
            }

            var value = line[(colon + 1)..].Trim();
            switch (line[..colon].Trim().ToUpperInvariant())
            {
                case "ALGORITHM":
                    listAlgorithm = value;
                    break;
                case "HASH":
                    listHash = value;
                    break;
                case "PATH":
                    listPath = value;
                    break;
            }
        }

        return listHash is not null
            && IsSha256(listHash)
            && (listAlgorithm is null || listAlgorithm.Equals("SHA256", StringComparison.OrdinalIgnoreCase))
            && (listPath is null || FileName(listPath) == assetName)
                ? listHash.ToLowerInvariant()
                : null;
    }

    private static bool IsSha256(string value) => value.Length == 64 && value.All(char.IsAsciiHexDigit);

    // Platform-independent: the listing may carry a Windows path.
    private static string FileName(string path) => path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
}
