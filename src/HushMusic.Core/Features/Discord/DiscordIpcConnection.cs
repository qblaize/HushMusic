using System.IO.Pipes;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace HushMusic.Core.Features.Discord;

/// <summary>No Discord desktop app answered on its pipes (it isn't running, or it hung up).</summary>
internal sealed class DiscordUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>Discord closed the connection during the handshake, e.g. code 4000 for an unknown application id.</summary>
internal sealed class DiscordRejectedException(int? code, string? reason)
    : Exception($"Discord refused the connection ({code?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no code"}: {reason ?? "no reason"}).")
{
    public int? Code { get; } = code;
}

/// <summary>
/// One connection to the Discord desktop app over its named pipe: the handshake, SET_ACTIVITY commands, and a read loop
/// that answers pings and notices when Discord hangs up. Writes are serialized; everything runs on background threads.
/// </summary>
internal sealed class DiscordIpcConnection : IAsyncDisposable
{
    /// <summary>Discord numbers its pipes 0 to 9; the first one that answers is used.</summary>
    internal const int PipeCount = 10;

    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly NamedPipeClientStream _pipe;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private Task _readLoop = Task.CompletedTask;
    private int _errorsLogged;
    private int _disposed;

    private DiscordIpcConnection(NamedPipeClientStream pipe, ILogger logger)
    {
        _pipe = pipe;
        _logger = logger;
    }

    /// <summary>Completes when the connection is over: Discord hung up, the pipe broke, or it was disposed.</summary>
    public Task Closed => _readLoop;

    /// <summary>
    /// Connects to the first of <paramref name="pipeName"/>-0 … -9 that answers and completes the handshake. Throws
    /// <see cref="DiscordUnavailableException"/> when none does, <see cref="DiscordRejectedException"/> when Discord refuses
    /// the application id.
    /// </summary>
    public static async Task<DiscordIpcConnection> ConnectAsync(string pipeName, string applicationId, ILogger logger, CancellationToken cancellationToken)
    {
        for (var i = 0; i < PipeCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var pipe = new NamedPipeClientStream(".", $"{pipeName}-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                // A zero timeout is a single attempt: a missing pipe fails at once instead of being polled.
                pipe.Connect(0);
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                continue;
            }

            var connection = new DiscordIpcConnection(pipe, logger);
            try
            {
                await connection.HandshakeAsync(applicationId, cancellationToken).ConfigureAwait(false);
                return connection;
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        throw new DiscordUnavailableException("The Discord desktop app isn't running.");
    }

    /// <summary>Sets (or, with null, clears) this process's activity.</summary>
    public Task SetActivityAsync(int processId, JsonNode? activity, CancellationToken cancellationToken) =>
        WriteAsync(DiscordOpcode.Frame, DiscordRpcMessages.SetActivity(processId, activity, Guid.NewGuid().ToString()), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _closing.Cancel();
        await _pipe.DisposeAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "The Discord read loop ended with an error");
        }

        _closing.Dispose();
        _writeGate.Dispose();
    }

    private async Task HandshakeAsync(string applicationId, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HandshakeTimeout);
        DiscordFrame? reply;
        try
        {
            await WriteAsync(DiscordOpcode.Handshake, DiscordRpcMessages.Handshake(applicationId), timeout.Token).ConfigureAwait(false);
            reply = await DiscordIpcCodec.ReadAsync(_pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DiscordUnavailableException("Discord didn't answer the handshake.");
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            throw new DiscordUnavailableException("The Discord pipe broke during the handshake.", ex);
        }

        switch (reply)
        {
            case null:
                throw new DiscordUnavailableException("Discord hung up during the handshake.");
            case { Opcode: DiscordOpcode.Close }:
                var close = DiscordRpcMessages.TryParse(reply.Payload);
                throw new DiscordRejectedException(close?.Code, close?.Message);
            case { Opcode: DiscordOpcode.Frame } when DiscordRpcMessages.IsReady(reply.Payload):
                _readLoop = Task.Run(() => ReadLoopAsync(_closing.Token), CancellationToken.None);
                return;
            default:
                throw new DiscordUnavailableException($"Unexpected handshake reply (opcode {reply.Opcode}).");
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await DiscordIpcCodec.ReadAsync(_pipe, cancellationToken).ConfigureAwait(false);
                switch (frame?.Opcode)
                {
                    case null:
                        _logger.LogDebug("Discord closed the pipe");
                        return;
                    case DiscordOpcode.Ping:
                        // A pong echoes the ping's payload.
                        await WriteAsync(DiscordOpcode.Pong, frame.Payload, cancellationToken).ConfigureAwait(false);
                        break;
                    case DiscordOpcode.Close:
                        var close = DiscordRpcMessages.TryParse(frame.Payload);
                        _logger.LogDebug("Discord closed the connection: {Code} {Message}", close?.Code, close?.Message);
                        return;
                    case DiscordOpcode.Frame:
                        Inspect(frame.Payload);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ObjectDisposedException)
        {
            _logger.LogDebug(ex, "The Discord connection broke");
        }
    }

    private void Inspect(string payload)
    {
        if (DiscordRpcMessages.TryParse(payload) is not { IsError: true } reply)
        {
            return;
        }

        if (Interlocked.Increment(ref _errorsLogged) == 1)
        {
            _logger.LogWarning("Discord refused a status update: {Code} {Message}", reply.Code, reply.Message);
        }
        else
        {
            _logger.LogDebug("Discord refused a status update: {Code} {Message}", reply.Code, reply.Message);
        }
    }

    private async Task WriteAsync(DiscordOpcode opcode, string payload, CancellationToken cancellationToken)
    {
        var frame = DiscordIpcCodec.Encode(opcode, payload);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}
