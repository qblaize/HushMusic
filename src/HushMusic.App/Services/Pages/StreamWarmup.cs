using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.App.Services.Pages;

/// <summary>
/// Resolves streams before the user clicks, so playback starts without waiting for yt-dlp (~2.5 s per track):
/// tracks the pointer rests on, and the first track of a page that was just shown.
/// </summary>
public interface IStreamWarmup
{
    /// <summary>The pointer entered a track; it is resolved if the pointer stays a moment.</summary>
    void HoverStarted(Track? track);

    /// <summary>The pointer left the track before the delay elapsed.</summary>
    void HoverEnded(Track? track);

    /// <summary>Resolves the track now (e.g. the first track of a page that was just loaded).</summary>
    void Warm(Track? track);
}

internal sealed class StreamWarmup(IStreamResolver resolver, ILogger<StreamWarmup> logger) : IStreamWarmup
{
    // Long enough that sweeping the pointer across a list doesn't start a yt-dlp run per row.
    private static readonly TimeSpan HoverDelay = TimeSpan.FromMilliseconds(400);

    // Speculative runs only; the player's own next-track prefetch goes straight to the resolver and is never throttled.
    private readonly SemaphoreSlim _slots = new(2, 2);
    private readonly Lock _gate = new();
    private CancellationTokenSource? _hover;
    private string? _hoverVideoId;

    public void HoverStarted(Track? track)
    {
        if (!IsPlayable(track))
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_hoverVideoId == track!.VideoId)
            {
                return;
            }

            _hover?.Cancel();
            cts = _hover = new CancellationTokenSource();
            _hoverVideoId = track.VideoId;
        }

        _ = WarmAfterDelayAsync(track.VideoId, cts.Token);
    }

    public void HoverEnded(Track? track)
    {
        lock (_gate)
        {
            if (track is not null && _hoverVideoId == track.VideoId)
            {
                _hover?.Cancel();
                _hover = null;
                _hoverVideoId = null;
            }
        }
    }

    public void Warm(Track? track)
    {
        if (IsPlayable(track))
        {
            _ = WarmAsync(track!.VideoId);
        }
    }

    private static bool IsPlayable(Track? track) => track is { IsAvailable: true } && !string.IsNullOrEmpty(track.VideoId);

    private async Task WarmAfterDelayAsync(string videoId, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(HoverDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        await WarmAsync(videoId).ConfigureAwait(false);
    }

    private async Task WarmAsync(string videoId)
    {
        if (!_slots.Wait(0))
        {
            logger.LogDebug("Skipping warm-up of {VideoId}: all warm-up slots are busy", videoId);
            return;
        }

        try
        {
            // Not cancelled when the pointer leaves: the user often comes back, and a finished resolve is cached for hours.
            await resolver.ResolveAsync(videoId, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Warm-up of {VideoId} failed", videoId);
        }
        finally
        {
            _slots.Release();
        }
    }
}
