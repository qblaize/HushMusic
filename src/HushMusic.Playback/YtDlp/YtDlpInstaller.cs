using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using HushMusic.Core;

namespace HushMusic.Playback.YtDlp;

/// <summary>An app-managed yt-dlp build: its release tag (also its folder name) and executable.</summary>
internal sealed record InstalledYtDlp(string Tag, string ExecutablePath);

/// <summary>
/// Installs the official "onedir" yt-dlp build (yt-dlp_win.zip) into <c>tools\yt-dlp\&lt;version&gt;\</c>, verified against
/// the release's SHA2-256SUMS. The onedir build starts ~0.6 s faster per run than the single-file yt-dlp.exe (which
/// unpacks itself on every launch), but yt-dlp cannot self-update it (<c>-U</c> refuses "unpackaged executables"),
/// so updates are a fresh install into a new version folder.
/// </summary>
internal static class YtDlpInstaller
{
    private const string ReleasesBase = "https://github.com/yt-dlp/yt-dlp/releases/";
    private const string ExecutableName = "yt-dlp.exe";

    private static string AssetName => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "yt-dlp_win_arm64.zip",
        Architecture.X86 => "yt-dlp_win_x86.zip",
        _ => "yt-dlp_win.zip",
    };

    /// <summary>The newest installed version under <paramref name="root"/>, or null when none is installed.</summary>
    public static InstalledYtDlp? FindInstalled(string root)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        return Directory.EnumerateDirectories(root)
            .Select(dir => new InstalledYtDlp(Path.GetFileName(dir), Path.Combine(dir, ExecutableName)))
            .Where(v => TryParseTag(v.Tag, out _) && File.Exists(v.ExecutablePath))
            .OrderByDescending(v => ParseTag(v.Tag))
            .FirstOrDefault();
    }

    /// <summary>The tag of the latest stable release.</summary>
    public static async Task<string> GetLatestTagAsync(CancellationToken cancellationToken)
    {
        var (tag, status) = await ManagedTools.GetLatestTagAsync(ReleasesBase, cancellationToken).ConfigureAwait(false);
        return tag is not null && TryParseTag(tag, out _)
            ? tag
            : throw new HushException($"Could not find the latest yt-dlp release (HTTP {(int)status}).");
    }

    /// <summary>True when <paramref name="candidate"/> is a newer release than <paramref name="installed"/>.</summary>
    public static bool IsNewer(string candidate, string? installed) =>
        installed is null || !TryParseTag(installed, out var current) || (TryParseTag(candidate, out var next) && next > current);

    /// <summary>Downloads, verifies and extracts release <paramref name="tag"/>. Returns the path of its yt-dlp.exe.</summary>
    public static async Task<string> InstallAsync(string root, string tag, ILogger logger, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var target = Path.Combine(root, tag);
        var exe = Path.Combine(target, ExecutableName);
        if (File.Exists(exe))
        {
            return exe;
        }

        using var http = ManagedTools.CreateClient(allowRedirect: true);
        var releaseBase = $"{ReleasesBase}download/{tag}/";
        var sums = await http.GetStringAsync(releaseBase + "SHA2-256SUMS", cancellationToken).ConfigureAwait(false);
        var expected = FindHash(sums, AssetName)
            ?? throw new HushException($"The yt-dlp {tag} release has no checksum for {AssetName}.");

        var hash = await ManagedTools.InstallZipAsync(http, releaseBase + AssetName, expected, root, tag, ExecutableName, logger, cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("Installed yt-dlp {Tag} to {Path} (SHA-256 {Hash})", tag, target, hash);
        return exe;
    }

    /// <summary>
    /// Removes every version except <paramref name="keepTag"/>, leftovers of interrupted installs, and the single-file
    /// yt-dlp.exe older app versions used. Folders still in use by a running yt-dlp are skipped and retried next time.
    /// </summary>
    public static void RemoveOtherVersions(string root, string toolsFolder, string keepTag, ILogger logger)
    {
        ManagedTools.TryDeleteFile(Path.Combine(toolsFolder, ExecutableName));
        ManagedTools.RemoveOtherVersions(root, keepTag, logger);
    }

    /// <summary>Finds the hash for <paramref name="fileName"/> in a "&lt;sha256&gt;  &lt;file name&gt;" listing.</summary>
    internal static string? FindHash(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[1].TrimStart('*') == fileName && parts[0].Length == 64)
            {
                return parts[0];
            }
        }

        return null;
    }

    // Release tags look like "2026.08.19" (optionally ".1" for a same-day re-release).
    private static bool TryParseTag(string tag, out Version version) => Version.TryParse(tag, out version!) && version.Major >= 2020;

    private static Version ParseTag(string tag) => TryParseTag(tag, out var version) ? version : new Version(0, 0);
}
