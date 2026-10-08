using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Features;

/// <summary>
/// Adds every track that starts playing to the signed-in user's YouTube Music history.
/// Reference example of a feature: it only listens to <see cref="IPlayer"/> events and calls a Core API.
/// </summary>
public sealed class PlayHistoryReporter(
    IPlayer player,
    IAccountApi accountApi,
    IAuthService auth,
    ISettingsService settings,
    ILogger<PlayHistoryReporter> logger) : IHostedService, IDisposable
{
    private readonly CancellationTokenSource _stopping = new();

    public Task StartAsync(CancellationToken cancellationToken)
    {
        player.TrackStarted += OnTrackStarted;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        player.TrackStarted -= OnTrackStarted;
        _stopping.Cancel();
        return Task.CompletedTask;
    }

    public void Dispose() => _stopping.Dispose();

    private void OnTrackStarted(object? sender, TrackChangedEventArgs e)
    {
        // Live radio stations are not YouTube videos: nothing to report.
        if (e.Track is not { IsLiveRadio: false } track || !settings.Current.ReportPlaybackHistory || auth.Status != AuthStatus.SignedIn)
        {
            return;
        }

        _ = ReportAsync(track.VideoId);
    }

    private async Task ReportAsync(string videoId)
    {
        try
        {
            await accountApi.AddHistoryItemAsync(videoId, _stopping.Token).ConfigureAwait(false);
            logger.LogDebug("Reported {VideoId} to play history", videoId);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // History is best effort: log it, don't bother the user with an InfoBar for every track.
            logger.LogWarning(ex, "Could not add {VideoId} to play history", videoId);
        }
    }
}
