using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HushMusic.Core.Features.Discord;

/// <summary>Frame opcodes of Discord's local RPC pipe (as in discord-rpc and discord-rpc-csharp).</summary>
public enum DiscordOpcode
{
    Handshake = 0,
    Frame = 1,
    Close = 2,
    Ping = 3,
    Pong = 4,
}

/// <summary>One frame on the pipe: its opcode and its UTF-8 JSON payload.</summary>
public sealed record DiscordFrame(DiscordOpcode Opcode, string Payload);

/// <summary>
/// The pipe's framing: a little-endian int32 opcode, a little-endian int32 payload length, then the payload as UTF-8 JSON.
/// </summary>
public static class DiscordIpcCodec
{
    public const int HeaderSize = 8;

    /// <summary>discord-rpc's MaxRpcFrameSize: the header and payload together never exceed 64 KiB.</summary>
    public const int MaxFrameSize = 64 * 1024;

    public static byte[] Encode(DiscordOpcode opcode, string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var length = Encoding.UTF8.GetByteCount(payload);
        if (length > MaxFrameSize - HeaderSize)
        {
            throw new ArgumentException($"A Discord frame carries at most {MaxFrameSize - HeaderSize} bytes.", nameof(payload));
        }

        var frame = new byte[HeaderSize + length];
        BinaryPrimitives.WriteInt32LittleEndian(frame, (int)opcode);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), length);
        Encoding.UTF8.GetBytes(payload, frame.AsSpan(HeaderSize));
        return frame;
    }

    /// <summary>
    /// Reads one frame. Null when the stream ended cleanly before it; <see cref="EndOfStreamException"/> when it ended
    /// inside one, <see cref="InvalidDataException"/> for an unknown opcode or an oversized length.
    /// </summary>
    public static async Task<DiscordFrame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var header = new byte[HeaderSize];
        var read = await stream.ReadAtLeastAsync(header, HeaderSize, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        if (read < HeaderSize)
        {
            throw new EndOfStreamException("The Discord pipe closed inside a frame header.");
        }

        var opcode = BinaryPrimitives.ReadInt32LittleEndian(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (opcode is < (int)DiscordOpcode.Handshake or > (int)DiscordOpcode.Pong)
        {
            throw new InvalidDataException($"Unknown Discord opcode {opcode}.");
        }

        if (length is < 0 or > MaxFrameSize - HeaderSize)
        {
            throw new InvalidDataException($"Discord frame length {length} is out of range.");
        }

        var payload = new byte[length];
        if (length > 0)
        {
            await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        return new DiscordFrame((DiscordOpcode)opcode, Encoding.UTF8.GetString(payload));
    }
}

/// <summary>The JSON messages Hush sends and the replies it reads (Discord RPC, protocol version 1).</summary>
public static class DiscordRpcMessages
{
    public const int ProtocolVersion = 1;

    /// <summary>The first frame on a new connection (opcode <see cref="DiscordOpcode.Handshake"/>).</summary>
    public static string Handshake(string applicationId) =>
        new JsonObject { ["v"] = ProtocolVersion, ["client_id"] = applicationId }.ToJsonString();

    /// <summary>
    /// SET_ACTIVITY for this process (opcode <see cref="DiscordOpcode.Frame"/>). A null <paramref name="activity"/> clears
    /// the status.
    /// </summary>
    public static string SetActivity(int processId, JsonNode? activity, string nonce) =>
        new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = processId, ["activity"] = activity?.DeepClone() },
            ["nonce"] = nonce,
        }.ToJsonString();

    /// <summary>The handshake reply that means the connection is ready: <c>{"cmd":"DISPATCH","evt":"READY",…}</c>.</summary>
    public static bool IsReady(string payload) =>
        TryParse(payload) is { } reply && reply.Command == "DISPATCH" && reply.Event == "READY";

    /// <summary>The command, event and error of a reply; null when it isn't a JSON object.</summary>
    public static DiscordReply? TryParse(string payload)
    {
        try
        {
            if (JsonNode.Parse(payload) is not JsonObject json)
            {
                return null;
            }

            var data = json["data"] as JsonObject;
            return new DiscordReply(
                Text(json["cmd"]),
                Text(json["evt"]),
                Text(json["nonce"]),
                Number(data?["code"]) ?? Number(json["code"]),
                Text(data?["message"]) ?? Text(json["message"]));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Number(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
}

/// <summary>A reply from Discord: a command's result, an event, or (on a Close frame) just a code and a message.</summary>
public sealed record DiscordReply(string? Command, string? Event, string? Nonce, int? Code, string? Message)
{
    public bool IsError => Event == "ERROR";
}
