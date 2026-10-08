using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using HushMusic.Core;

namespace HushMusic.Playback.YtDlp;

/// <summary>
/// Shared plumbing for the app-managed tools (yt-dlp, Deno): GitHub release downloads, checksum-verified extraction into
/// <c>&lt;root&gt;\&lt;version&gt;\</c> folders, and removal of old versions. Version folders mean an update never touches
/// files a running process has open.
/// </summary>
internal static class ManagedTools
{
    private const string ExtractingSuffix = ".extracting";
    private const string DeletingSuffix = ".delete";
    private const long MaxDownloadBytes = 200L * 1024 * 1024;

    public static HttpClient CreateClient(bool allowRedirect)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = allowRedirect,
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromMinutes(5),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("HushMusic/1.0");
        return http;
    }

    /// <summary>
    /// The tag of a repository's latest stable release, read from the "releases/latest" redirect (no API rate limit).
    /// Null, with the HTTP status, when there was no redirect.
    /// </summary>
    public static async Task<(string? Tag, HttpStatusCode Status)> GetLatestTagAsync(string releasesBase, CancellationToken cancellationToken)
    {
        using var http = CreateClient(allowRedirect: false);
        using var request = new HttpRequestMessage(HttpMethod.Head, releasesBase + "latest");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var location = response.Headers.Location?.ToString();
        return (location is null ? null : location[(location.LastIndexOf('/') + 1)..], response.StatusCode);
    }

    /// <summary>
    /// Downloads the zip at <paramref name="url"/>, checks it against <paramref name="expectedSha256"/> and extracts it into
    /// <c>&lt;root&gt;\&lt;folderName&gt;\</c>, which must then contain <paramref name="executableName"/>. Returns the hash.
    /// </summary>
    public static async Task<string> InstallZipAsync(
        HttpClient http,
        string url,
        string expectedSha256,
        string root,
        string folderName,
        string executableName,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var assetName = url[(url.LastIndexOf('/') + 1)..];
        var target = Path.Combine(root, folderName);
        var zipPath = Path.Combine(root, $"{folderName}-{assetName}.download");
        var extracting = target + ExtractingSuffix;
        try
        {
            var actual = await DownloadAsync(http, url, zipPath, assetName, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("{Asset} checksum mismatch: expected {Expected}, got {Actual}", assetName, expectedSha256, actual);
                throw new HushException($"The downloaded {assetName} failed its checksum check and was discarded.");
            }

            TryDeleteDirectory(extracting);

            // ExtractToDirectory rejects entries that would land outside the target folder.
            await Task.Run(() => ZipFile.ExtractToDirectory(zipPath, extracting), cancellationToken).ConfigureAwait(false);
            if (!File.Exists(Path.Combine(extracting, executableName)))
            {
                throw new HushException($"{assetName} did not contain {executableName}.");
            }

            Directory.Move(extracting, target);
            return actual;
        }
        finally
        {
            TryDeleteFile(zipPath);
            TryDeleteDirectory(extracting);
        }
    }

    /// <summary>
    /// Removes every folder under <paramref name="root"/> except <paramref name="keepFolder"/>, including leftovers of
    /// interrupted installs. Folders still in use by a running process are skipped and retried next time.
    /// </summary>
    public static void RemoveOtherVersions(string root, string keepFolder, ILogger logger)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(root).ToList())
        {
            if (string.Equals(Path.GetFileName(dir), keepFolder, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Rename first: it fails while a running process has files open, so a folder in use is never half-deleted.
            var doomed = dir.EndsWith(DeletingSuffix, StringComparison.Ordinal) ? dir : dir + DeletingSuffix;
            try
            {
                if (!string.Equals(doomed, dir, StringComparison.Ordinal))
                {
                    Directory.Move(dir, doomed);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug("Old tool folder {Path} is still in use; will retry later", dir);
                continue;
            }

            TryDeleteDirectory(doomed);
        }
    }

    public static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static bool TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }

            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> DownloadAsync(HttpClient http, string url, string path, string assetName, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
            {
                throw new HushException($"The {assetName} download is unexpectedly large and was aborted.");
            }

            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
