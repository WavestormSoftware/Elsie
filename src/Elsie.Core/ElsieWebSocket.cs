using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;

namespace Elsie;

/// <summary>Limits and conformance switches for <see cref="ElsieWebSocket"/>.</summary>
public sealed class ElsieWebSocketOptions
{
    /// <summary>Largest message (all fragments combined) accepted from the client. Default 16 MiB.</summary>
    public long MaxMessageBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>Largest single frame accepted from the client. Default 16 MiB.</summary>
    public long MaxFrameBytes { get; set; } = 16 * 1024 * 1024;

    /// <summary>
    /// Reject unmasked client frames with a 1002 protocol close (RFC 6455 §5.1 requires this for
    /// servers). Default true; disable only for non-conformant test clients.
    /// </summary>
    public bool RequireMaskedFrames { get; set; } = true;

    /// <summary>Validate UTF-8 in text messages and close with 1007 when invalid. Default true.</summary>
    public bool ValidateUtf8 { get; set; } = true;

    /// <summary>Auto-reply to ping frames with a pong. Default true.</summary>
    public bool AutoPong { get; set; } = true;
}

/// <summary>Raised when a peer violates the WebSocket protocol; the connection is failed with <see cref="CloseStatus"/>.</summary>
public sealed class ElsieWebSocketException : Exception
{
    public ElsieWebSocketException(WebSocketCloseStatus closeStatus, string message)
        : base(message)
    {
        CloseStatus = closeStatus;
    }

    public WebSocketCloseStatus CloseStatus { get; }
}

/// <summary>
/// WebSocket connection over a duplex stream (RFC 6455 framing): fragmented messages, interleaved
/// control frames, masked-frame enforcement, UTF-8 validation, close handshake with status/reason,
/// subprotocol reporting and serialized sends (safe for concurrent broadcasters).
/// </summary>
public sealed class ElsieWebSocket : IAsyncDisposable
{
    private const byte OpContinuation = 0x0;
    private const byte OpText = 0x1;
    private const byte OpBinary = 0x2;
    private const byte OpClose = 0x8;
    private const byte OpPing = 0x9;
    private const byte OpPong = 0xA;

    private static readonly UTF8Encoding Utf8Strict = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly bool _leaveOpen;
    private readonly ElsieWebSocketOptions _options;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private volatile bool _closed;
    private bool _closeSent;
    private long _bytesSent;
    private long _bytesReceived;

    public ElsieWebSocket(
        Stream stream,
        bool leaveOpen = false,
        ElsieWebSocketOptions? options = null,
        string? subprotocol = null)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _leaveOpen = leaveOpen;
        _options = options ?? new ElsieWebSocketOptions();
        Subprotocol = subprotocol;
    }

    /// <summary>Negotiated subprotocol (null when none was requested/selected).</summary>
    public string? Subprotocol { get; }

    /// <summary>True once the connection is closed or failed.</summary>
    public bool IsClosed => _closed;

    /// <summary>Status received from the peer's close frame (null when not received).</summary>
    public WebSocketCloseStatus? CloseStatus { get; private set; }

    /// <summary>Reason received from the peer's close frame.</summary>
    public string? CloseStatusDescription { get; private set; }

    /// <summary>Bytes written to the connection (payload only).</summary>
    public long BytesSent => Interlocked.Read(ref _bytesSent);

    /// <summary>Bytes read from the connection (payload only).</summary>
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    public Task SendTextAsync(string text, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        return SendFrameAsync(OpText, Utf8Strict.GetBytes(text), cancellationToken);
    }

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default) =>
        SendFrameAsync(OpBinary, data, cancellationToken);

    /// <summary>Send a ping (payload ≤125 bytes).</summary>
    public Task PingAsync(ReadOnlyMemory<byte> payload = default, CancellationToken cancellationToken = default)
    {
        if (payload.Length > 125)
        {
            throw new ArgumentException("Control frame payload must be 125 bytes or fewer.", nameof(payload));
        }

        return SendFrameAsync(OpPing, payload, cancellationToken);
    }

    /// <summary>
    /// Send a close frame with an optional status and reason (truncated to 123 UTF-8 bytes), then
    /// mark the connection closed. Safe to call more than once. Does not wait for the peer's echo.
    /// </summary>
    public async Task CloseAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? reason = null,
        CancellationToken cancellationToken = default)
    {
        if (_closeSent)
        {
            return;
        }

        await SendCloseFrameAsync(status, reason, cancellationToken).ConfigureAwait(false);
        _closed = true;
    }

    /// <summary>Alias for <see cref="CloseAsync"/> (the send side is closed; the peer may still reply).</summary>
    public Task CloseOutputAsync(
        WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure,
        string? reason = null,
        CancellationToken cancellationToken = default) =>
        CloseAsync(status, reason, cancellationToken);

    /// <summary>
    /// Read the next complete data message. Fragments are assembled, control frames handled.
    /// Returns null when the peer closed (or the connection ended).
    /// </summary>
    public async Task<ElsieWebSocketMessage?> ReceiveAsync(CancellationToken cancellationToken = default)
    {
        byte messageOpcode = 0;
        MemoryStream? assembled = null;

        while (!_closed)
        {
            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
            {
                _closed = true;
                return null;
            }

            Interlocked.Add(ref _bytesReceived, frame.Payload.Length);

            switch (frame.Opcode)
            {
                case OpContinuation:
                    if (messageOpcode == 0)
                    {
                        throw await FailAsync(WebSocketCloseStatus.ProtocolError, "Unexpected continuation frame.").ConfigureAwait(false);
                    }

                    Append(assembled!, frame.Payload);
                    if (assembled!.Length > _options.MaxMessageBytes)
                    {
                        throw await FailAsync(WebSocketCloseStatus.MessageTooBig, "WebSocket message too large.").ConfigureAwait(false);
                    }

                    if (frame.Fin)
                    {
                        return CompleteMessage(messageOpcode, assembled.ToArray());
                    }

                    break;

                case OpText:
                case OpBinary:
                    if (messageOpcode != 0)
                    {
                        throw await FailAsync(WebSocketCloseStatus.ProtocolError, "New data frame during a fragmented message.").ConfigureAwait(false);
                    }

                    if (frame.Payload.Length > _options.MaxMessageBytes)
                    {
                        throw await FailAsync(WebSocketCloseStatus.MessageTooBig, "WebSocket message too large.").ConfigureAwait(false);
                    }

                    if (frame.Fin)
                    {
                        return CompleteMessage(frame.Opcode, frame.Payload);
                    }

                    messageOpcode = frame.Opcode;
                    assembled = new MemoryStream(frame.Payload.Length * 2);
                    Append(assembled, frame.Payload);
                    break;

                case OpClose:
                    await HandleCloseFrameAsync(frame.Payload, cancellationToken).ConfigureAwait(false);
                    return null;

                case OpPing:
                    if (_options.AutoPong)
                    {
                        await SendFrameAsync(OpPong, frame.Payload, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case OpPong:
                    break;

                default:
                    throw await FailAsync(WebSocketCloseStatus.ProtocolError, $"Unsupported opcode 0x{frame.Opcode:X}.").ConfigureAwait(false);
            }
        }

        return null;
    }

    /// <summary>Read the next text message (null on close; throws for a binary message).</summary>
    public async Task<string?> ReceiveTextAsync(CancellationToken cancellationToken = default)
    {
        var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return null;
        }

        if (message.MessageType != WebSocketMessageType.Text)
        {
            throw new ElsieWebSocketException(WebSocketCloseStatus.InvalidMessageType, "Expected a text message.");
        }

        return message.GetText();
    }

    /// <summary>Read the next binary message (null on close; throws for a text message).</summary>
    public async Task<byte[]?> ReceiveBinaryAsync(CancellationToken cancellationToken = default)
    {
        var message = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
        if (message is null)
        {
            return null;
        }

        if (message.MessageType != WebSocketMessageType.Binary)
        {
            throw new ElsieWebSocketException(WebSocketCloseStatus.InvalidMessageType, "Expected a binary message.");
        }

        return message.Payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_closed)
        {
            try
            {
                await CloseAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Best effort on shutdown.
            }
        }

        _sendLock.Dispose();
        if (!_leaveOpen)
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ElsieWebSocketMessage CompleteMessage(byte opcode, byte[] payload)
    {
        if (opcode == OpText && _options.ValidateUtf8)
        {
            try
            {
                _ = Utf8Strict.GetString(payload);
            }
            catch (DecoderFallbackException)
            {
                throw new ElsieWebSocketException(WebSocketCloseStatus.InvalidPayloadData, "Text message is not valid UTF-8.");
            }
        }

        return new ElsieWebSocketMessage(
            opcode == OpText ? WebSocketMessageType.Text : WebSocketMessageType.Binary,
            payload);
    }

    private static void Append(MemoryStream stream, byte[] payload) => stream.Write(payload, 0, payload.Length);

    private async Task HandleCloseFrameAsync(byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length == 1)
        {
            throw new ElsieWebSocketException(WebSocketCloseStatus.ProtocolError, "Close frame payload must be empty or ≥2 bytes.");
        }

        if (payload.Length >= 2)
        {
            var code = BinaryPrimitives.ReadUInt16BigEndian(payload);
            CloseStatus = (WebSocketCloseStatus)code;
            if (payload.Length > 2)
            {
                CloseStatusDescription = Utf8Strict.GetString(payload, 2, payload.Length - 2);
            }
        }
        else
        {
            CloseStatus = WebSocketCloseStatus.Empty;
        }

        var replyStatus = CloseStatus == WebSocketCloseStatus.Empty
            ? WebSocketCloseStatus.NormalClosure
            : CloseStatus!.Value;
        if (!_closeSent)
        {
            await SendCloseFrameAsync(replyStatus, reason: null, cancellationToken).ConfigureAwait(false);
        }

        _closed = true;
    }

    private async Task<ElsieWebSocketException> FailAsync(WebSocketCloseStatus status, string message)
    {
        if (!_closeSent)
        {
            try
            {
                await SendCloseFrameAsync(status, message, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
            {
                // Peer already gone.
            }
        }

        _closed = true;
        return new ElsieWebSocketException(status, message);
    }

    private Task SendCloseFrameAsync(WebSocketCloseStatus status, string? reason, CancellationToken cancellationToken)
    {
        // 1005/1006/1015 are reserved and must never appear on the wire.
        if (status is WebSocketCloseStatus.Empty or WebSocketCloseStatus.EndpointUnavailable)
        {
            status = WebSocketCloseStatus.NormalClosure;
        }

        var reasonBytes = string.IsNullOrEmpty(reason)
            ? Array.Empty<byte>()
            : TruncateUtf8(reason, 123);
        var payload = new byte[2 + reasonBytes.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)status);
        reasonBytes.CopyTo(payload, 2);

        _closeSent = true;
        return SendFrameAsync(OpClose, payload, cancellationToken);
    }

    private static byte[] TruncateUtf8(string value, int maxBytes)
    {
        var bytes = Utf8Strict.GetBytes(value);
        if (bytes.Length <= maxBytes)
        {
            return bytes;
        }

        var length = maxBytes;
        while (length > 0 && (bytes[length] & 0xC0) == 0x80)
        {
            length--;
        }

        return bytes[..length];
    }

    private async Task SendFrameAsync(byte opcode, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (_closed && opcode != OpClose)
        {
            throw new InvalidOperationException("WebSocket is closed.");
        }

        var header = new byte[10];
        header[0] = (byte)(0x80 | (opcode & 0x0F));
        int headerLength;
        if (payload.Length < 126)
        {
            header[1] = (byte)payload.Length;
            headerLength = 2;
        }
        else if (payload.Length <= ushort.MaxValue)
        {
            header[1] = 126;
            BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), (ushort)payload.Length);
            headerLength = 4;
        }
        else
        {
            header[1] = 127;
            BinaryPrimitives.WriteInt64BigEndian(header.AsSpan(2), payload.Length);
            headerLength = 10;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header.AsMemory(0, headerLength), cancellationToken).ConfigureAwait(false);
            if (payload.Length > 0)
            {
                await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _bytesSent, payload.Length);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<WsFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        var header = new byte[2];
        if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var fin = (header[0] & 0x80) != 0;
        var rsv = header[0] & 0x70;
        var opcode = (byte)(header[0] & 0x0F);
        var masked = (header[1] & 0x80) != 0;
        long payloadLength = header[1] & 0x7F;

        if (rsv != 0)
        {
            throw await FailAsync(WebSocketCloseStatus.ProtocolError, "RSV bits set without a negotiated extension.").ConfigureAwait(false);
        }

        if (payloadLength == 126)
        {
            var extended = new byte[2];
            if (!await ReadExactAsync(extended, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            payloadLength = BinaryPrimitives.ReadUInt16BigEndian(extended);
        }
        else if (payloadLength == 127)
        {
            var extended = new byte[8];
            if (!await ReadExactAsync(extended, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            payloadLength = BinaryPrimitives.ReadInt64BigEndian(extended);
            if (payloadLength < 0)
            {
                throw await FailAsync(WebSocketCloseStatus.ProtocolError, "Invalid 64-bit frame length.").ConfigureAwait(false);
            }
        }

        var isControl = opcode >= 0x8;
        if (isControl)
        {
            if (!fin)
            {
                throw await FailAsync(WebSocketCloseStatus.ProtocolError, "Fragmented control frame.").ConfigureAwait(false);
            }

            if (payloadLength > 125)
            {
                throw await FailAsync(WebSocketCloseStatus.ProtocolError, "Control frame payload exceeds 125 bytes.").ConfigureAwait(false);
            }
        }

        if (payloadLength > _options.MaxFrameBytes)
        {
            throw await FailAsync(WebSocketCloseStatus.MessageTooBig, "WebSocket frame too large.").ConfigureAwait(false);
        }

        if (_options.RequireMaskedFrames && !masked)
        {
            throw await FailAsync(WebSocketCloseStatus.ProtocolError, "Client frames must be masked.").ConfigureAwait(false);
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

        var payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
        if (payloadLength > 0 && !await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false))
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

        return new WsFrame(fin, opcode, payload);
    }

    private async Task<bool> ReadExactAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private sealed record WsFrame(bool Fin, byte Opcode, byte[] Payload);
}

/// <summary>A complete text or binary WebSocket message.</summary>
public sealed class ElsieWebSocketMessage
{
    public ElsieWebSocketMessage(WebSocketMessageType messageType, byte[] payload)
    {
        MessageType = messageType;
        Payload = payload ?? throw new ArgumentNullException(nameof(payload));
    }

    public WebSocketMessageType MessageType { get; }
    public byte[] Payload { get; }

    public bool IsText => MessageType == WebSocketMessageType.Text;
    public int Length => Payload.Length;

    public string GetText() => Encoding.UTF8.GetString(Payload);
}
