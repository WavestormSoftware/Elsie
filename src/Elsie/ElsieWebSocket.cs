using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Elsie;

/// <summary>Minimal WebSocket façade over a duplex stream (RFC 6455 framing).</summary>
public sealed class ElsieWebSocket : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private bool _closed;

    public ElsieWebSocket(Stream stream, bool leaveOpen = false)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
    }

    public bool IsClosed => _closed;

    public async Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var bytes = Encoding.UTF8.GetBytes(text);
        await SendFrameAsync(opcode: 0x1, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await SendFrameAsync(opcode: 0x2, data, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string? reason = null, CancellationToken cancellationToken = default)
    {
        if (_closed)
        {
            return;
        }

        var reasonBytes = string.IsNullOrEmpty(reason)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(reason);
        var payload = new byte[2 + reasonBytes.Length];
        payload[0] = (byte)(((int)status >> 8) & 0xFF);
        payload[1] = (byte)((int)status & 0xFF);
        if (reasonBytes.Length > 0)
        {
            Buffer.BlockCopy(reasonBytes, 0, payload, 2, reasonBytes.Length);
        }

        await SendFrameAsync(opcode: 0x8, payload, cancellationToken).ConfigureAwait(false);
        _closed = true;
    }

    /// <summary>Read the next data frame (text/binary). Control frames are handled automatically.</summary>
    public async Task<ElsieWebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        while (!_closed)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                _closed = true;
                return null;
            }

            switch (frame.Opcode)
            {
                case 0x1: // text
                    return new ElsieWebSocketMessage(WebSocketMessageType.Text, frame.Payload);
                case 0x2: // binary
                    return new ElsieWebSocketMessage(WebSocketMessageType.Binary, frame.Payload);
                case 0x8: // close
                {
                    if (!_closed)
                    {
                        // Echo close frame then mark closed
                        await SendFrameAsync(0x8, frame.Payload, cancellationToken).ConfigureAwait(false);
                        _closed = true;
                    }

                    return null;
                }
                case 0x9: // ping
                    await SendFrameAsync(0xA, frame.Payload, cancellationToken).ConfigureAwait(false);
                    break;
                case 0xA: // pong
                    break;
                default:
                    break;
            }
        }

        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task SendFrameAsync(byte opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_closed && opcode != 0x8)
        {
            throw new InvalidOperationException("WebSocket is closed.");
        }

        // Server frames are not masked
        var header = new List<byte>(10) { (byte)(0x80 | (opcode & 0x0F)) };
        if (payload.Length < 126)
        {
            header.Add((byte)payload.Length);
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header.Add(126);
            header.Add((byte)((payload.Length >> 8) & 0xFF));
            header.Add((byte)(payload.Length & 0xFF));
        }
        else
        {
            header.Add(127);
            var len = (long)payload.Length;
            for (var i = 7; i >= 0; i--)
            {
                header.Add((byte)((len >> (8 * i)) & 0xFF));
            }
        }

        await _stream.WriteAsync(header.ToArray(), cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
        {
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        }

        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<WsFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fin = (header[0] & 0x80) != 0;
        var opcode = (byte)(header[0] & 0x0F);
        var masked = (header[1] & 0x80) != 0;
        long payloadLen = header[1] & 0x7F;

        if (payloadLen == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(ext, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            payloadLen = (ext[0] << 8) | ext[1];
        }
        else if (payloadLen == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(ext, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            payloadLen = 0;
            for (var i = 0; i < 8; i++)
            {
                payloadLen = (payloadLen << 8) | ext[i];
            }
        }

        if (payloadLen > int.MaxValue || payloadLen > 16 * 1024 * 1024)
        {
            throw new InvalidOperationException("WebSocket frame too large.");
        }

        byte[]? mask = null;
        if (masked)
        {
            mask = new byte[4];
            if (!await ReadExactAsync(mask, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
        }

        var payload = payloadLen == 0 ? Array.Empty<byte>() : new byte[payloadLen];
        if (payloadLen > 0 && !await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        if (mask is not null)
        {
            for (var i = 0; i < payload.Length; i++)
            {
                payload[i] ^= mask[i % 4];
            }
        }

        if (!fin && opcode is 0x1 or 0x2)
        {
            // Simplified: require FIN for data frames in v1
            throw new InvalidOperationException("Fragmented WebSocket frames are not supported yet.");
        }

        return new WsFrame(opcode, payload);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                return false;
            }

            offset += n;
        }

        return true;
    }

    private sealed record WsFrame(byte Opcode, byte[] Payload);
}

public sealed class ElsieWebSocketMessage
{
    public ElsieWebSocketMessage(WebSocketMessageType messageType, byte[] payload)
    {
        MessageType = messageType;
        Payload = payload;
    }

    public WebSocketMessageType MessageType { get; }
    public byte[] Payload { get; }

    public string GetText() => Encoding.UTF8.GetString(Payload);
}
