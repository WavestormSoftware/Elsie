namespace Elsie.Web.Http2;

internal enum Http2FrameType : byte
{
    Data = 0x0,
    Headers = 0x1,
    Priority = 0x2,
    RstStream = 0x3,
    Settings = 0x4,
    PushPromise = 0x5,
    Ping = 0x6,
    GoAway = 0x7,
    WindowUpdate = 0x8,
    Continuation = 0x9
}

[Flags]
internal enum Http2FrameFlags : byte
{
    None = 0,
    EndStream = 0x1,
    Ack = 0x1,
    EndHeaders = 0x4,
    Padded = 0x8,
    Priority = 0x20
}

internal readonly struct Http2Frame
{
    public Http2Frame(Http2FrameType type, Http2FrameFlags flags, int streamId, byte[] payload)
    {
        Type = type;
        Flags = flags;
        StreamId = streamId;
        Payload = payload;
    }

    public Http2FrameType Type { get; }
    public Http2FrameFlags Flags { get; }
    public int StreamId { get; }
    public byte[] Payload { get; }
}

internal static class Http2FrameIo
{
    public static readonly byte[] ClientPreface =
        "PRI * HTTP/2.0\r\n\r\nSM\r\n\r\n"u8.ToArray();

    public static async Task WriteFrameAsync(
        Stream stream,
        Http2FrameType type,
        Http2FrameFlags flags,
        int streamId,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        var len = payload.Length;
        if (len > 0xFFFFFF)
        {
            throw new InvalidOperationException("HTTP/2 frame too large.");
        }

        var header = new byte[9];
        header[0] = (byte)((len >> 16) & 0xFF);
        header[1] = (byte)((len >> 8) & 0xFF);
        header[2] = (byte)(len & 0xFF);
        header[3] = (byte)type;
        header[4] = (byte)flags;
        header[5] = (byte)((streamId >> 24) & 0x7F);
        header[6] = (byte)((streamId >> 16) & 0xFF);
        header[7] = (byte)((streamId >> 8) & 0xFF);
        header[8] = (byte)(streamId & 0xFF);

        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        if (len > 0)
        {
            await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }
    }

    public static async Task<Http2Frame?> ReadFrameAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[9];
        if (!await ReadExactAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var length = (header[0] << 16) | (header[1] << 8) | header[2];
        var type = (Http2FrameType)header[3];
        var flags = (Http2FrameFlags)header[4];
        var streamId = ((header[5] & 0x7F) << 24) | (header[6] << 16) | (header[7] << 8) | header[8];

        var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
        if (length > 0 && !await ReadExactAsync(stream, payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new Http2Frame(type, flags, streamId, payload);
    }

    private static async Task<bool> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
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
