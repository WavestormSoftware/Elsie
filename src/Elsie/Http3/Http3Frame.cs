using System.Buffers.Binary;

namespace Elsie.Web.Http3;

/// <summary>RFC 9000 §16 variable-length integer encoding.</summary>
internal static class QuicVarInt
{
    /// <summary>
    /// Reads a QUIC varint. Returns the value (≤ <see cref="int.MaxValue"/>) or -1 when the
    /// value exceeds <see cref="int.MaxValue"/> (a protocol error for HTTP/3 frame lengths,
    /// never a <see cref="OverflowException"/>). Throws <see cref="InvalidOperationException"/>
    /// for truncated input.
    /// </summary>
    public static int Read(ReadOnlySpan<byte> data, out int consumed)
    {
        if (data.Length == 0)
        {
            throw new InvalidOperationException("Truncated QUIC varint.");
        }

        var first = data[0];
        var prefix = first >> 6;
        var length = 1 << prefix; // 1, 2, 4, 8
        if (data.Length < length)
        {
            throw new InvalidOperationException("Truncated QUIC varint.");
        }

        ulong value = (ulong)(first & 0x3F);
        for (var i = 1; i < length; i++)
        {
            value = (value << 8) | data[i];
        }

        consumed = length;
        return value > int.MaxValue ? -1 : (int)value;
    }

    public static int EncodedLength(ulong value)
    {
        if (value < (1UL << 6))
        {
            return 1;
        }

        if (value < (1UL << 14))
        {
            return 2;
        }

        if (value < (1UL << 30))
        {
            return 4;
        }

        return 8;
    }

    public static int Write(Span<byte> dest, ulong value)
    {
        var len = EncodedLength(value);
        var prefix = len switch
        {
            1 => 0b00,
            2 => 0b01,
            4 => 0b10,
            _ => 0b11
        };

        dest[0] = (byte)(((ulong)prefix << 6) | (value >> (8 * (len - 1))));
        for (var i = 1; i < len; i++)
        {
            dest[i] = (byte)(value >> (8 * (len - 1 - i)));
        }

        return len;
    }
}

/// <summary>HTTP/3 frame types (RFC 9114 §7.2).</summary>
internal enum Http3FrameType : long
{
    Data = 0x0,
    Headers = 0x1,
    CancelPush = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    GoAway = 0x7,
    MaxPushId = 0xD
}

/// <summary>RFC 9114 frame header + payload (type + length varints).</summary>
internal readonly struct Http3Frame
{
    public Http3Frame(Http3FrameType type, ReadOnlyMemory<byte> payload)
    {
        Type = type;
        Payload = payload;
    }

    public Http3FrameType Type { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    /// <summary>Total encoded length of the frame.</summary>
    public int EncodedLength =>
        QuicVarInt.EncodedLength((ulong)Type)
        + QuicVarInt.EncodedLength((ulong)Payload.Length)
        + Payload.Length;

    public void Write(Span<byte> dest)
    {
        var offset = QuicVarInt.Write(dest, (ulong)Type);
        offset += QuicVarInt.Write(dest[offset..], (ulong)Payload.Length);
        Payload.Span.CopyTo(dest[offset..]);
    }

    public static Http3Frame Parse(ReadOnlySpan<byte> data)
    {
        var typeValue = QuicVarInt.Read(data, out var typeConsumed);
        var offset = typeConsumed;
        var length = QuicVarInt.Read(data[offset..], out var lengthConsumed);
        offset += lengthConsumed;
        if (data.Length - offset < length)
        {
            throw new InvalidOperationException("Truncated HTTP/3 frame payload.");
        }

        return new Http3Frame((Http3FrameType)typeValue, data.Slice(offset, length).ToArray());
    }
}

/// <summary>HTTP/3 connection-error codes (RFC 9114 §8.1).</summary>
internal static class Http3ErrorCodes
{
    public const long NoError = 0x100;
    public const long GeneralProtocolError = 0x101;
    public const long ClosedCriticalStream = 0x104;
    public const long FrameUnexpected = 0x105;
    public const long ExcessiveLoad = 0x107;
}

/// <summary>
/// Raised when a peer violates the HTTP/3 framing rules. Carries the RFC 9114 §8.1
/// connection-error code the connection must be closed with.
/// </summary>
internal sealed class Http3ProtocolException : Exception
{
    public Http3ProtocolException(string message, long errorCode = Http3ErrorCodes.GeneralProtocolError)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    /// <summary>The connection error code to close with (e.g. H3_FRAME_UNEXPECTED).</summary>
    public long ErrorCode { get; }
}

/// <summary>Reads HTTP/3 frames from a QUIC stream.</summary>
internal static class Http3FrameReader
{
    /// <summary>Absolute cap on any single frame payload we will buffer (defends against
    /// malicious length varints; typical DATA frames are ≤ 16 KiB and header blocks ≤ 64 KiB).</summary>
    public const int MaxFramePayload = 64 * 1024 * 1024;

    /// <summary>
    /// Reads one frame from <paramref name="stream"/>. Returns false on end-of-stream.
    /// Throws <see cref="Http3ProtocolException"/> for invalid varints or oversized payloads.
    /// </summary>
    public static async Task<Http3Frame?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var first = new byte[1];
        var read = await ReadExactlyAsync(stream, first, 1, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        // Length of the type varint follows from the first byte's 2-bit prefix.
        var typeLen = 1 << (first[0] >> 6);
        var header = new byte[typeLen];
        header[0] = first[0];
        if (typeLen > 1)
        {
            read = await ReadExactlyAsync(stream, header.AsMemory(1, typeLen - 1), typeLen - 1, cancellationToken)
                .ConfigureAwait(false);
            if (read < typeLen - 1)
            {
                return null;
            }
        }

        var typeValue = QuicVarInt.Read(header.AsSpan(0, typeLen), out _);
        if (typeValue < 0)
        {
            throw new Http3ProtocolException("HTTP/3 frame type exceeds int range.");
        }

        var lenFirst = new byte[1];
        read = await ReadExactlyAsync(stream, lenFirst, 1, cancellationToken).ConfigureAwait(false);
        if (read == 0)
        {
            return null;
        }

        var lenPrefix = lenFirst[0] >> 6;
        var lenLen = 1 << lenPrefix;
        var payloadLenHeader = new byte[lenLen];
        payloadLenHeader[0] = lenFirst[0];
        if (lenLen > 1)
        {
            read = await ReadExactlyAsync(stream, payloadLenHeader.AsMemory(1, lenLen - 1), lenLen - 1, cancellationToken)
                .ConfigureAwait(false);
            if (read < lenLen - 1)
            {
                return null;
            }
        }

        var length = QuicVarInt.Read(payloadLenHeader.AsSpan(0, lenLen), out _);
        if (length < 0 || length > MaxFramePayload)
        {
            throw new Http3ProtocolException(
                $"HTTP/3 frame payload length {length} is invalid or exceeds the {MaxFramePayload}-byte cap.");
        }

        var payload = new byte[length];
        read = await ReadExactlyAsync(stream, payload, length, cancellationToken).ConfigureAwait(false);
        if (read < length)
        {
            return null;
        }

        return new Http3Frame((Http3FrameType)typeValue, payload);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, Memory<byte> buffer, int count, CancellationToken ct)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}

/// <summary>Writes HTTP/3 frames to a QUIC stream.</summary>
internal static class Http3FrameWriter
{
    public static async Task WriteAsync(
        Stream stream,
        Http3Frame frame,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[frame.EncodedLength];
        frame.Write(buffer);
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
    }
}
