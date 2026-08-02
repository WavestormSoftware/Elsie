namespace Elsie.Grpc;

/// <summary>Thrown when a gRPC message framing violation is detected.</summary>
public sealed class GrpcFrameException(string message) : Exception(message);

/// <summary>
/// gRPC message framing over HTTP streams: each message is prefixed with a 5-byte header
/// (1 compression-flag + length byte, then a 4-byte big-endian length).
/// </summary>
public static class GrpcFraming
{
    /// <summary>Size of the gRPC frame header in bytes.</summary>
    public const int FrameHeaderSize = 5;

    /// <summary>Reads one framed message from <paramref name="stream"/>; returns null at end of stream.</summary>
    public static async Task<byte[]?> ReadMessageAsync(
        Stream stream,
        int maxMessageSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var header = new byte[FrameHeaderSize];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var compressed = (header[0] & 0x80) != 0;
        var length = ((long)header[1] << 24)
            | ((long)header[2] << 16)
            | ((long)header[3] << 8)
            | header[4];

        if (length > maxMessageSize)
        {
            throw new GrpcFrameException(
                $"Incoming gRPC message of {length} bytes exceeds the {maxMessageSize}-byte limit.");
        }

        if (compressed)
        {
            throw new GrpcFrameException(
                "Compressed gRPC messages are not supported; the client must send uncompressed frames.");
        }

        var payload = new byte[length];
        if (length > 0 && !await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            throw new GrpcFrameException("Truncated gRPC message payload.");
        }

        return payload;
    }

    /// <summary>Writes one framed (uncompressed) message to <paramref name="stream"/>.</summary>
    public static async Task WriteMessageAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var length = payload.Length;
        if (length > int.MaxValue)
        {
            throw new GrpcFrameException("gRPC message too large.");
        }

        var header = new byte[FrameHeaderSize];
        header[0] = 0x00; // uncompressed
        header[1] = (byte)(length >> 24);
        header[2] = (byte)(length >> 16);
        header[3] = (byte)(length >> 8);
        header[4] = (byte)length;

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (length > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            offset += n;
        }

        return true;
    }
}
