using System.Net.Quic;

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
/// HTTP/3 connection-error codes for QPACK violations (RFC 9114 §8.1 / RFC 9204 §2.2).
/// </summary>
internal static class Http3QpackErrorCodes
{
    public const long DecompressionFailed = 0x200;
    public const long EncoderStreamError = 0x201;
    public const long DecoderStreamError = 0x202;
}

/// <summary>
/// HTTP/3 control streams: server SETTINGS/GOAWAY/MAX_PUSH_ID and client control / QPACK
/// stream reading (RFC 9114 §6.2 + §7.2.4). The server advertises a nonzero QPACK dynamic
/// table capacity (configurable via <see cref="ElsieServerOptions"/>) and forwards the
/// client's encoder/decoder stream bytes to the per-connection <see cref="QpackDecoder"/>
/// and <see cref="QpackEncoder"/>.
/// </summary>
internal static class Http3ControlStreams
{
    public const ulong SettingsQpackMaxTableCapacity = 0x1;
    public const ulong SettingsMaxFieldSectionSize = 0x6;
    public const ulong SettingsQpackBlockedStreams = 0x7;
    public const ulong SettingsEnableConnectProtocol = 0x8;

    /// <summary>Writes the server control-stream preamble (type + SETTINGS).</summary>
    public static async Task WriteServerPreambleAsync(
        Stream stream,
        ElsieServerOptions serverOptions,
        CancellationToken cancellationToken)
    {
        await WriteUnidirectionalTypeAsync(stream, Http3UnidirectionalStreamType.Control, cancellationToken)
            .ConfigureAwait(false);

        using var payload = new MemoryStream();
        WriteSetting(payload, SettingsQpackMaxTableCapacity, (ulong)Math.Max(0, serverOptions.QpackMaxTableCapacity));
        WriteSetting(payload, SettingsQpackBlockedStreams, (ulong)Math.Max(0, serverOptions.QpackBlockedStreams));
        WriteSetting(payload, SettingsMaxFieldSectionSize, 16 * 1024);
        // RFC 9220 §3: Extended CONNECT (WebSocket over HTTP/3).
        WriteSetting(payload, SettingsEnableConnectProtocol, 1);

        await Http3FrameWriter.WriteAsync(
            stream,
            new Http3Frame(Http3FrameType.Settings, payload.ToArray()),
            cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the client's unidirectional streams (control + QPACK encoder/decoder) until
    /// end-of-stream. Frames from the control stream are consumed (SETTINGS forwarded to the
    /// encoder); QPACK stream bytes are fed to the codecs. QPACK instruction violations are
    /// NOT swallowed — they poison the per-connection codec state, so the connection is
    /// terminated with the matching RFC 9114 §8.1 error code.
    /// </summary>
    public static async Task ReadClientUnidirectionalStreamAsync(
        Stream stream,
        QpackDecoder decoder,
        QpackEncoder encoder,
        QuicConnection connection,
        CancellationToken cancellationToken)
    {
        var isEncoderStream = false;
        try
        {
            var typeBuffer = new byte[8];
            var read = await ReadExactlyAsync(stream, typeBuffer, 1, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return;
            }

            var type = (Http3UnidirectionalStreamType)QuicVarInt.Read(typeBuffer.AsSpan(0, 1), out _);
            switch (type)
            {
                case Http3UnidirectionalStreamType.Control:
                    await ReadControlFramesAsync(stream, encoder, cancellationToken).ConfigureAwait(false);
                    return;

                case Http3UnidirectionalStreamType.QpackEncoder:
                    isEncoderStream = true;
                    await DrainAndFeedAsync(stream, decoder, cancellationToken).ConfigureAwait(false);
                    return;

                case Http3UnidirectionalStreamType.QpackDecoder:
                    await DrainAndFeedAsync(stream, encoder, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    // Push (0x1) and unknown types: drain to EOF so the peer's flow control
                    // is not blocked (RFC 9114 §6.2).
                    await DrainAsync(stream, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (QpackException)
        {
            // RFC 9114 §8.1: the peer sent an instruction our codec cannot interpret. A
            // poisoned encoder/decoder buffer would fail every subsequent stream on the
            // connection, so terminate it with the matching error code instead.
            await CloseWithErrorAsync(
                connection,
                isEncoderStream ? Http3QpackErrorCodes.EncoderStreamError : Http3QpackErrorCodes.DecoderStreamError,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // One bad unidirectional stream must not take down the connection.
        }
    }

    /// <summary>Reads SETTINGS frames from the client control stream (others skipped).</summary>
    private static async Task ReadControlFramesAsync(Stream stream, QpackEncoder encoder, CancellationToken cancellationToken)
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
                ParseSettings(frame.Value.Payload.Span, encoder);
            }
            // GOAWAY / MAX_PUSH_ID / CANCEL_PUSH from the client are ignored.
        }
    }

    /// <summary>Parses a SETTINGS payload (IDs/values are varints); forwards QPACK settings.</summary>
    private static void ParseSettings(ReadOnlySpan<byte> payload, QpackEncoder encoder)
    {
        var pos = 0;
        while (pos < payload.Length)
        {
            pos += QuicVarInt.Read(payload[pos..], out var id);
            pos += QuicVarInt.Read(payload[pos..], out var value);
            if ((ulong)id == SettingsQpackMaxTableCapacity)
            {
                // The client's decoder capacity is the limit for our encoder's dynamic table.
                encoder.SetPeerMaxTableCapacity(value);
            }
        }
    }

    /// <summary>Reads bytes from a QPACK encoder/decoder stream and feeds them to a codec.</summary>
    private static async Task DrainAndFeedAsync(
        Stream stream,
        object codec,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            var n = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                return;
            }

            if (codec is QpackDecoder decoder)
            {
                decoder.ProcessEncoderStream(buffer.AsSpan(0, n));
                await decoder.DrainDecoderInstructionsAsync(cancellationToken).ConfigureAwait(false);
            }
            else if (codec is QpackEncoder encoder)
            {
                encoder.ProcessDecoderStream(buffer.AsSpan(0, n));
                await encoder.FlushEncoderInstructionsAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
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

    private static async Task CloseWithErrorAsync(
        QuicConnection connection,
        long errorCode,
        CancellationToken cancellationToken)
    {
        try
        {
#pragma warning disable CA1416 // QUIC is only reachable from the platform-guarded connection path
            await connection.CloseAsync(errorCode, cancellationToken).ConfigureAwait(false);
#pragma warning restore CA1416
        }
        catch (Exception ex) when (ex is QuicException or ObjectDisposedException or OperationCanceledException)
        {
            // Connection already closing or aborted — nothing to do.
        }
    }
}
