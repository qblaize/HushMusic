using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using HushMusic.Core.Abstractions;
using HushMusic.Core.Models;

namespace HushMusic.Core.Radio;

/// <summary>
/// Follows the ICY title of the station that is playing by polling it (<see cref="IcyMetadataReader"/>): one short
/// connection every <see cref="PollInterval"/>, the first right away. Runs only between <see cref="Follow"/> and
/// <see cref="Unfollow"/>, which the player calls as the station starts and stops.
/// </summary>
/// <remarks>
/// Each poll downloads the audio up to the first metadata block (8–45 KB at typical icy-metaint values, plus what the server
/// pushes before the connection closes; the socket's small receive buffer keeps that bounded). That is a few KB/s on
/// average instead of a second full stream (16–40 KB/s). Stations without metadata stop being polled after the first try.
/// </remarks>
public sealed class RadioNowPlayingService(IHttpClientFactory httpClientFactory, ILogger<RadioNowPlayingService> logger)
    : IRadioNowPlaying, IDisposable
{
    public const string HttpClientName = "RadioMetadata";

    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);

    // Stations that only ever send an empty title are polled less often.
    private static readonly TimeSpan QuietPollInterval = TimeSpan.FromSeconds(60);
    private const int QuietAfterEmptyPolls = 3;
    private const int MaxFailures = 3;

    // A small TCP receive window: the server can't push much more audio than is read before the poll disconnects.
    private const int ReceiveBufferBytes = 32 * 1024;

    private readonly Lock _gate = new();
    private RadioStation? _station;
    private RadioNowPlaying? _current;
    private CancellationTokenSource? _pollCts;
    private bool _disposed;

    public event EventHandler<RadioNowPlayingChangedEventArgs>? Changed;

    public RadioNowPlaying? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>The metadata connections' handler: no pooling worth keeping (each poll aborts mid-stream), a small receive buffer.</summary>
    public static HttpMessageHandler CreateHttpHandler() => new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(8),
        PooledConnectionLifetime = TimeSpan.FromMinutes(1),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveBufferSize = ReceiveBufferBytes };
            try
            {
                await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public void Follow(RadioStation station)
    {
        ArgumentNullException.ThrowIfNull(station);
        CancellationTokenSource? previous;
        CancellationTokenSource cts;
        string? cleared = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (_station?.Id == station.Id && _pollCts is not null)
            {
                return; // already following
            }

            if (_station is { } old && old.Id != station.Id && _current is not null)
            {
                cleared = old.Id;
                _current = null;
            }

            _station = station;
            previous = _pollCts;
            cts = new CancellationTokenSource();
            _pollCts = cts;
        }

        previous?.Cancel();
        if (cleared is not null)
        {
            Changed?.Invoke(this, new RadioNowPlayingChangedEventArgs(cleared, null));
        }

        _ = Task.Run(() => PollAsync(station, cts.Token));
    }

    public void Unfollow(bool forget)
    {
        CancellationTokenSource? previous;
        string? cleared = null;
        lock (_gate)
        {
            previous = _pollCts;
            _pollCts = null;
            if (forget)
            {
                if (_current is not null && _station is { } station)
                {
                    cleared = station.Id;
                }

                _current = null;
                _station = null;
            }
        }

        previous?.Cancel();
        if (cleared is not null)
        {
            Changed?.Invoke(this, new RadioNowPlayingChangedEventArgs(cleared, null));
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? previous;
        lock (_gate)
        {
            _disposed = true;
            previous = _pollCts;
            _pollCts = null;
        }

        previous?.Cancel();
    }

    private async Task PollAsync(RadioStation station, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out var url))
        {
            return;
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var failures = 0;
        var emptyPolls = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                var result = await IcyMetadataReader.ReadAsync(client, url, cancellationToken).ConfigureAwait(false);
                failures = 0;
                if (!result.HasMetadata)
                {
                    logger.LogInformation("{Station} sends no ICY metadata; showing the station name", station.Name);
                    return;
                }

                var nowPlaying = IcyMetadata.ToNowPlaying(station, result.StreamTitle, result.StreamUrl);
                emptyPolls = nowPlaying is null ? emptyPolls + 1 : 0;
                Publish(station, nowPlaying);
                wait = emptyPolls >= QuietAfterEmptyPolls ? QuietPollInterval : PollInterval;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                failures++;
                logger.LogDebug(ex, "Reading the ICY title of {Station} failed ({Failures}/{Max})", station.Name, failures, MaxFailures);
                if (failures >= MaxFailures)
                {
                    logger.LogInformation("Stopped reading titles of {Station} after {Failures} failures", station.Name, failures);
                    return;
                }

                wait = PollInterval * failures;
            }

            try
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void Publish(RadioStation station, RadioNowPlaying? nowPlaying)
    {
        lock (_gate)
        {
            // Stale poll (another station, or unfollowed meanwhile), or nothing new.
            if (_station?.Id != station.Id || _pollCts is null || Equals(_current, nowPlaying))
            {
                return;
            }

            _current = nowPlaying;
        }

        if (nowPlaying is not null)
        {
            logger.LogDebug("{Station} now plays \"{Title}\"", station.Name, nowPlaying.StreamTitle);
        }

        Changed?.Invoke(this, new RadioNowPlayingChangedEventArgs(station.Id, nowPlaying));
    }
}
