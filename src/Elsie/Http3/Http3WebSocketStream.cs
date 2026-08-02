using System.Net.Quic;

namespace Elsie.Web.Http3;

/// <summary>
/// Duplex stream adapter that wraps HTTP/3 request stream DATA frames into a byte-stream
/// surface, so <see cref="ElsieWebSocket"/> (RFC 6455 framing) can run over an HTTP/3
/// stream after a successful extended CONNECT handshake (RFC 9220).
/// Reads: buffers the payload of each incoming DATA frame and serves partial reads.
/// Writes: wraps each write call into an HTTP/3 DATA frame.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[System.Runtime.Versioning.SupportedOSPlatform("macos")]
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class Http3WebSocketStream : Stream
{
    private readonly QuicStream _stream;
    private readonly CancellationToken _cancellationToken;
    private byte[]? _pending;
    private int _offset;

    public Http3WebSocketStream(QuicStream stream, CancellationToken cancellationToken)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken;
        while (_pending is null || _offset >= _pending.Length)
        {
            var frame = await Http3FrameReader.ReadAsync(_stream, token).ConfigureAwait(false);
            if (frame is null)
            {
                return 0; // end of stream
            }

            // DATA frames carry WebSocket message bytes; HEADERS frames are trailers (ignored).
            if (frame.Value.Type == Http3FrameType.Data)
            {
                _pending = frame.Value.Payload.ToArray();
                _offset = 0;
            }
        }

        var take = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, take).CopyTo(buffer);
        _offset += take;
        return take;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken;
        await Http3FrameWriter.WriteAsync(_stream, new Http3Frame(Http3FrameType.Data, buffer.ToArray()), token)
            .ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
