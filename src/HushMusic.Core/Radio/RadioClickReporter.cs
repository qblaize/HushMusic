using Microsoft.Extensions.Hosting;
using HushMusic.Core.Abstractions;

namespace HushMusic.Core.Radio;

/// <summary>
/// Counts a play on Radio Browser (<c>/json/url/{stationuuid}</c>) each time a directory station starts playing, as the
/// API asks clients to do; that counter is what ranks stations by popularity. Best effort, in the background.
/// </summary>
public sealed class RadioClickReporter(IPlayer player, IRadioDirectory directory) : IHostedService, IDisposable
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
        if (e.Track?.Station is { IsInDirectory: true } station && !_stopping.IsCancellationRequested)
        {
            _ = directory.CountClickAsync(station.Id, _stopping.Token);
        }
    }
}
