using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.App.Services.Shell;

/// <summary>Disk cache for album art and avatars under <see cref="IAppPaths.ImageCache"/>. Thread-safe; all I/O runs off the UI thread.</summary>
public interface IImageCache
{
    /// <summary>
    /// Returns the local file path of the image, downloading it first when needed.
    /// Returns null for URLs that cannot be cached (non-http). Concurrent calls for the same URL share one download.
    /// </summary>
    Task<string?> GetLocalPathAsync(string url, CancellationToken cancellationToken = default);

    /// <summary>Deletes the cached copy (e.g. when it turned out to be undecodable).</summary>
    void Remove(string url);
}

public sealed class ImageCache : IImageCache, IDisposable
{
    public const string HttpClientName = "ImageCache";

    private const long MaxCacheBytes = 512L * 1024 * 1024;
    private const long TrimTargetBytes = 384L * 1024 * 1024;
    private const long MaxImageBytes = 20L * 1024 * 1024;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<ImageCache> _logger;
    private readonly string _folder;
    private readonly ConcurrentDictionary<string, Lazy<Task<string>>> _inFlight = new(StringComparer.Ordinal);

    // Files this session has already found or downloaded: art shown again (a page or the window coming back) is
    // answered at once, without a trip to the thread pool and the disk.
    private readonly ConcurrentDictionary<string, string> _known = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private int _trimStarted;

    public ImageCache(IHttpClientFactory httpClientFactory, IAppPaths paths, ILogger<ImageCache> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _folder = paths.ImageCache;
    }

    public async Task<string?> GetLocalPathAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!TryNormalize(url, out var uri))
        {
            return null;
        }

        StartTrimOnce();
        var key = KeyOf(uri);
        if (_known.TryGetValue(key, out var known))
        {
            return known;
        }

        var path = Path.Combine(_folder, key);

        // The download is shared, so one caller cancelling must not cancel it for the others.
        var download = _inFlight.GetOrAdd(key, k => new Lazy<Task<string>>(() => Task.Run(() => FetchAsync(k, uri, path))));
        return await download.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Remove(string url)
    {
        if (!TryNormalize(url, out var uri))
        {
            return;
        }

        var key = KeyOf(uri);
        _known.TryRemove(key, out _);
        var path = Path.Combine(_folder, key);
        _ = Task.Run(() =>
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Could not delete cached image {Path}", path);
            }
        });
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    internal static bool TryNormalize(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        // InnerTube sometimes returns protocol-relative thumbnail URLs.
        var candidate = url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttps && parsed.Scheme != Uri.UriSchemeHttp))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    private static string KeyOf(Uri uri) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri)));

    private async Task<string> FetchAsync(string key, Uri uri, string path)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            if (File.Exists(path))
            {
                _known[key] = path;
                return path;
            }

            var token = _lifetime.Token;
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Expected an image but got {mediaType}.");
            }

            if (response.Content.Headers.ContentLength > MaxImageBytes)
            {
                throw new InvalidDataException("Image is too large to cache.");
            }

            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                await response.Content.CopyToAsync(file, token).ConfigureAwait(false);
            }

            File.Move(temp, path, overwrite: true);
            _known[key] = path;
            return path;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private void StartTrimOnce()
    {
        if (Interlocked.Exchange(ref _trimStarted, 1) == 0)
        {
            _ = Task.Run(Trim);
        }
    }

    private void Trim()
    {
        try
        {
            var files = new DirectoryInfo(_folder).GetFiles();
            var staleTemp = DateTime.UtcNow.AddHours(-1);
            foreach (var temp in files.Where(f => f.Extension == ".tmp" && f.LastWriteTimeUtc < staleTemp))
            {
                TryDelete(temp.FullName);
            }

            var images = files.Where(f => f.Extension != ".tmp").ToList();
            var total = images.Sum(f => f.Length);
            if (total <= MaxCacheBytes)
            {
                return;
            }

            foreach (var file in images.OrderBy(f => f.LastWriteTimeUtc))
            {
                if (total <= TrimTargetBytes)
                {
                    break;
                }

                if (TryDelete(file.FullName))
                {
                    _known.TryRemove(file.Name, out _);
                    total -= file.Length;
                }
            }

            _logger.LogInformation("Image cache trimmed to {Megabytes} MB", total / (1024 * 1024));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Image cache trim failed");
        }
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
