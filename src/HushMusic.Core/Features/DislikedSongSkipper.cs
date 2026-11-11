using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Features;

/// <summary>
/// Disliking the song that is playing moves on to the next one, as YouTube Music does, whichever part of the app rated it.
/// Only listens to <see cref="IAccountActionsService.TrackRated"/>.
/// </summary>
public sealed partial class DislikedSongSkipper(IAccountActionsService account, IPlayer player, ILogger<DislikedSongSkipper> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        account.TrackRated += OnTrackRated;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        account.TrackRated -= OnTrackRated;
        return Task.CompletedTask;
    }

    private void OnTrackRated(object? sender, TrackRatedEventArgs e)
    {
        if (e.Status == LikeStatus.Dislike && player.CurrentTrack is { IsLiveRadio: false } current && current.VideoId == e.VideoId)
        {
            _ = SkipAsync(e.VideoId);
        }
    }

    private async Task SkipAsync(string videoId)
    {
        try
        {
            LogSkipping(logger, videoId);
            await player.NextAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not skip the disliked song");
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Skipping {VideoId}: disliked while playing")]
    private static partial void LogSkipping(ILogger logger, string videoId);
}
