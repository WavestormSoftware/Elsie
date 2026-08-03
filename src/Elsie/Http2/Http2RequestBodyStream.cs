using System.Threading.Channels;

namespace Elsie.Web.Http2;

/// <summary>
/// Streaming request-body stream for HTTP/2: DATA-frame payloads are fed by the connection
/// loop (see <c>OnDataAsync</c>) and surfaced through a bounded channel, so handlers can
/// process request bodies incrementally without waiting for END_STREAM. Required by
/// message-based protocols such as gRPC (grpc-go sends unary requests without Content-Length
/// and without half-closing the stream). Total size is capped at <c>MaxRequestBodyBytes</c>;
/// overflow marks the stream failed (the dispatcher maps it to 413 / close).
/// </summary>
internal sealed class Http2RequestBodyStream : Stream
{
    private readonly Channel<byte[]> _frames;
    private readonly long _maxBody;
    private long _receivedTotal;
    private bool _tooLarge;
    private bool _completed;
    private bool _failed;
    // Unconsumed remainder of a frame that did not fit the caller's buffer — served FIRST on
    // the next read (never pushed back onto the channel, which would reorder bytes).
    private byte[]? _pending;
    private int _pendingOffset;
    private int _pendingLength;

    public Http2RequestBodyStream(long maxBody)
    {
        _maxBody = maxBody > 0 ? maxBody : long.MaxValue;
        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    public bool TooLarge => _tooLarge;
    public long ReceivedBytes => _receivedTotal;

    /// <summary>Feeds one DATA-frame payload from the connection loop (single writer).</summary>
    public void Write(byte[] payload)
    {
        _receivedTotal += payload.Length;
        if (_receivedTotal > _maxBody)
        {
            _tooLarge = true;
            _frames.Writer.TryComplete();
            return;
        }

        _frames.Writer.TryWrite(payload);
    }

    /// <summary>Signals END_STREAM: no more DATA will arrive; readers see end-of-body.</summary>
    public void Complete()
    {
        if (!_completed)
        {
            _completed = true;
            _frames.Writer.TryComplete();
        }
    }

    /// <summary>Aborts (client disconnect / protocol error).</summary>
    public void Fail()
    {
        _failed = true;
        _frames.Writer.TryComplete();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (_pending is not null)
            {
                var take = Math.Min(destination.Length, _pendingLength - _pendingOffset);
                _pending.AsSpan(_pendingOffset, take).CopyTo(destination.Span);
                _pendingOffset += take;
                if (_pendingOffset >= _pendingLength)
                {
                    _pending = null;
                    _pendingOffset = 0;
                    _pendingLength = 0;
                }

                return take;
            }

            if (_frames.Reader.TryRead(out var chunk))
            {
                if (chunk.Length <= destination.Length)
                {
                    chunk.AsSpan().CopyTo(destination.Span);
                    return chunk.Length;
                }

                chunk.AsSpan(0, destination.Length).CopyTo(destination.Span);
                _pending = chunk;
                _pendingOffset = destination.Length;
                _pendingLength = chunk.Length;
                return destination.Length;
            }

            if (_frames.Reader.Completion.IsCompleted)
            {
                return _tooLarge || _failed ? -1 : 0;
            }

            var read = await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false);
            if (!read)
            {
                return _tooLarge || _failed ? -1 : 0;
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
