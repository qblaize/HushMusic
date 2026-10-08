using System.Globalization;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// Per-track loudness for volume normalization (<see cref="AppSettings.NormalizeVolume"/>), fetched with
/// <see cref="IWatchApi.GetLoudnessDbAsync"/> and cached per video id. The player applies the gain; this class only
/// knows the numbers. A fetch is shared by everyone waiting for the same track and is never cancelled by a waiter.
/// </summary>
public sealed class VolumeNormalizer(IWatchApi watchApi, ISettingsService settings, ILogger<VolumeNormalizer> logger)
{
    private const int MaxCachedTracks = 2000;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Task<double?>> _loudness = new(StringComparer.Ordinal);

    public bool IsEnabled => settings.Current.NormalizeVolume;

    /// <summary>True when the track's loudness has been fetched (it may still be unknown: null).</summary>
    public bool TryGetLoudness(string videoId, out double? loudnessDb)
    {
        if (LiveRadio.IsRadioId(videoId))
        {
            // Live radio has no YouTube loudness: known, and unknown.
            loudnessDb = null;
            return true;
        }

        lock (_gate)
        {
            if (_loudness.TryGetValue(videoId, out var task) && task.IsCompletedSuccessfully)
            {
                loudnessDb = task.Result;
                return true;
            }
        }

        loudnessDb = null;
        return false;
    }

    /// <summary>Loudness in dB relative to YouTube's reference, or null when unknown or the request failed. Only throws when cancelled.</summary>
    public Task<double?> GetLoudnessDbAsync(string videoId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(videoId);
        if (LiveRadio.IsRadioId(videoId))
        {
            return Task.FromResult<double?>(null);
        }

        var task = GetOrStartFetch(videoId);
        return cancellationToken.CanBeCanceled ? task.WaitAsync(cancellationToken) : task;
    }

    /// <summary>Fetches in the background (e.g. for the next queued track). Never throws.</summary>
    public void Prefetch(string videoId)
    {
        if (!string.IsNullOrEmpty(videoId) && !LiveRadio.IsRadioId(videoId))
        {
            _ = GetOrStartFetch(videoId);
        }
    }

    private Task<double?> GetOrStartFetch(string videoId)
    {
        TaskCompletionSource<double?> fetch;
        lock (_gate)
        {
            if (_loudness.TryGetValue(videoId, out var existing))
            {
                return existing;
            }

            if (_loudness.Count >= MaxCachedTracks)
            {
                _loudness.Clear();
            }

            fetch = new TaskCompletionSource<double?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _loudness[videoId] = fetch.Task;
        }

        _ = FetchAsync(videoId, fetch);
        return fetch.Task;
    }

    private async Task FetchAsync(string videoId, TaskCompletionSource<double?> fetch)
    {
        using var timeout = new CancellationTokenSource(FetchTimeout);
        try
        {
            var loudnessDb = await watchApi.GetLoudnessDbAsync(videoId, timeout.Token).ConfigureAwait(false);
            logger.LogDebug(
                "Loudness of {VideoId}: {LoudnessDb} dB, normalization gain {Gain}",
                videoId,
                loudnessDb?.ToString("0.00", CultureInfo.InvariantCulture) ?? "unknown",
                PlaybackGain.ForLoudness(loudnessDb).ToString("0.000", CultureInfo.InvariantCulture));
            fetch.TrySetResult(loudnessDb);
        }
        catch (Exception ex)
        {
            // Not cached, so the next load of this track asks again. Playback just runs at gain 1 meanwhile.
            logger.LogDebug(ex, "Could not get the loudness of {VideoId}", videoId);
            lock (_gate)
            {
                if (_loudness.TryGetValue(videoId, out var cached) && cached == fetch.Task)
                {
                    _loudness.Remove(videoId);
                }
            }

            fetch.TrySetResult(null);
        }
    }
}
