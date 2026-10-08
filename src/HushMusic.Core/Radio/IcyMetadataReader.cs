using System.Globalization;

namespace HushMusic.Core.Radio;

/// <summary>What one metadata read found.</summary>
/// <param name="HasMetadata">False when the server sends no ICY metadata at all (no icy-metaint header).</param>
/// <param name="StreamTitle">The StreamTitle field of the first non-empty block; null when none arrived in time.</param>
/// <param name="StreamUrl">The StreamUrl field of that block, when present.</param>
public sealed record IcyReadResult(bool HasMetadata, string? StreamTitle, string? StreamUrl)
{
    public static IcyReadResult NoMetadata { get; } = new(false, null, null);
}

/// <summary>
/// Reads the current ICY title of a stream on a connection of its own: asks for metadata (<c>Icy-MetaData: 1</c>), skips
/// <c>icy-metaint</c> bytes of audio, reads the metadata block that follows and disconnects. A server sends the current
/// title in the first block to a new listener, so a short read every so often is enough; staying connected would cost a
/// second copy of the stream's bandwidth.
/// </summary>
public static class IcyMetadataReader
{
    /// <summary>Audio bytes between metadata blocks above this are treated as no metadata (a broken header).</summary>
    public const int MaxMetaInterval = 256 * 1024;

    /// <summary>Blocks to read before giving up on a title (servers send an empty block when the title hasn't changed).</summary>
    public const int MaxBlocks = 3;

    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(12);

    public static async Task<IcyReadResult> ReadAsync(HttpClient client, Uri streamUrl, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(streamUrl);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ReadTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (!TryGetMetaInterval(response, out var interval))
            {
                return IcyReadResult.NoMetadata;
            }

            var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                return await ReadBlocksAsync(stream, interval, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("The stream sent no metadata in time.");
        }
    }

    /// <summary>Reads up to <see cref="MaxBlocks"/> metadata blocks from an ICY stream positioned at the start of the audio.</summary>
    public static async Task<IcyReadResult> ReadBlocksAsync(Stream stream, int interval, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(interval);
        var audio = new byte[Math.Min(interval, 16 * 1024)];
        var lengthByte = new byte[1];
        for (var block = 0; block < MaxBlocks; block++)
        {
            await SkipAsync(stream, interval, audio, cancellationToken).ConfigureAwait(false);
            await stream.ReadExactlyAsync(lengthByte, cancellationToken).ConfigureAwait(false);
            var length = lengthByte[0] * 16;
            if (length == 0)
            {
                continue;
            }

            var metadata = new byte[length];
            await stream.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
            var text = IcyMetadata.Decode(metadata);
            var title = IcyMetadata.GetField(text, "StreamTitle");
            if (title is not null)
            {
                return new IcyReadResult(true, title, IcyMetadata.GetField(text, "StreamUrl"));
            }
        }

        return new IcyReadResult(true, null, null);
    }

    private static bool TryGetMetaInterval(HttpResponseMessage response, out int interval)
    {
        interval = 0;
        return (response.Headers.TryGetValues("icy-metaint", out var values) || response.Content.Headers.TryGetValues("icy-metaint", out values))
            && int.TryParse(values.FirstOrDefault(), NumberStyles.Integer, CultureInfo.InvariantCulture, out interval)
            && interval is > 0 and <= MaxMetaInterval;
    }

    private static async Task SkipAsync(Stream stream, int count, byte[] buffer, CancellationToken cancellationToken)
    {
        while (count > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(count, buffer.Length)), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("The stream ended before its metadata.");
            }

            count -= read;
        }
    }
}
