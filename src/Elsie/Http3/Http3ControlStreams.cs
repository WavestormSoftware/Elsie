using System.Buffers.Binary;

namespace Elsie.Web.Http3;

/// <summary>Unidirectional stream types (RFC 9114 §6.2).</summary>
internal enum Http3UnidirectionalStreamType : long
{
    Control = 0x0,
    Push = 0x1,
    QpackEncoder = 0x2,
    QpackDecoder = 0x3
}

/// <summary>
/// HTTP/3 control streams: server SETTINGS/GOAWAY/MAX_PUSH_ID and client
/// control / QPACK stream reading (RFC 9114 §6.2 + §7.2.4).
/// </summary>
internal static class Http3ControlStreams
{
    public const ulong SettingsQpackMaxTableCapacity = 0x1;
    public const ulong SettingsMaxFieldSectionSize = 0x6;
    public const ulong SettingsQpackBlockedStreams = 0x7;

    /// <summary>Writes the server control-stream preamble (type + SETTINGS).</summary>
    public static async Task WriteServerPreambleAsync(Stream stream, CancellationToken cancellationToken)
    {
        await WriteUnidirectionalTypeAsync(stream, Http3UnidirectionalStreamType.Control, cancellationToken)
            .ConfigureAwait(false);

        // SETTINGS: QPACK max table capacity 0, blocked streams 0, max field section size 16 KiB.
        using var payload = new MemoryStream();
        WriteSetting(payload, SettingsQpackMaxTableCapacity, 0);
        WriteSetting(payload, SettingsQpackBlockedStreams, 0);
        WriteSetting(payload, SettingsMaxFieldSectionSize, 16 * 1024);

        await Http3FrameWriter.WriteAsync(
            stream,
            new Http3Frame(Http3FrameType.Settings, payload.ToArray()),
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the client's unidirectional streams (control + QPACK encoder/decoder) until
    /// end-of-stream. Frames from the control stream are consumed (SETTINGS tracked);
    /// QPACK stream bytes are discarded (we advertise capacity 0 so the client must not
    /// insert). Exceptions are swallowed so one bad stream cannot kill the connection loop.
    /// </summary>
    public static async Task ReadClientUnidirectionalStreamAsync(Stream stream, CancellationToken cancellationToken)
    {
        try
        {
            var typeBuffer = new byte[8];
            var read = await ReadExactlyAsync(stream, typeBuffer, 1, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            var type = (Http3UnidirectionalStreamType)QuicVarInt.Read(typeBuffer.AsSpan(0, 1), out _);
            if (type == Http3UnidirectionalStreamType.Control)
            {
                await ReadControlFramesAsync(stream, cancellationToken).ConfigureAwait(false);
                return;
            }

            // QPACK encoder (0x2) / decoder (0x3) streams: drain until EOF.
            var buffer = new byte[2048];
            while (!cancellationToken.IsCancellationRequested)
            {
                var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (n == 0)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // One bad unidirectional stream must not take down the connection.
        }
    }

    /// <summary>Reads SETTINGS frames from the client control stream (others skipped).</summary>
    private static async Task ReadControlFramesAsync(Stream stream, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await Http3FrameReader.ReadAsync(stream, cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                return; // end of stream
            }

            if (frame.Value.Type == Http3FrameType.Settings)
            {
                ParseSettings(frame.Value.Payload.Span);
            }
            // GOAWAY / MAX_PUSH_ID from the client are ignored for the minimal server.
        }
    }

    /// <summary>Parses a SETTINGS payload (IDs/values are varints).</summary>
    private static void ParseSettings(ReadOnlySpan<byte> payload)
    {
        var pos = 0;
        while (pos < payload.Length)
        {
            pos += QuicVarInt.Read(payload[pos..], out var id);
            pos += QuicVarInt.Read(payload[pos..], out var value);
            _ = id;
            _ = value;
            // With zero QPACK capacity advertised by us, client settings are informational.
        }
    }

    private static void WriteSetting(Stream payload, ulong id, ulong value)
    {
        Span<byte> buffer = stackalloc byte[16];
        var len = QuicVarInt.Write(buffer, id);
        len += QuicVarInt.Write(buffer[len..], value);
        payload.Write(buffer[..len]);
    }

    private static async Task WriteUnidirectionalTypeAsync(
        Stream stream,
        Http3UnidirectionalStreamType type,
        CancellationToken cancellationToken)
    {
        Span<byte> buffer = stackalloc byte[8];
        var len = QuicVarInt.Write(buffer, (ulong)type);
        await stream.WriteAsync(buffer[..len].ToArray(), cancellationToken).ConfigureAwait(false);
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
