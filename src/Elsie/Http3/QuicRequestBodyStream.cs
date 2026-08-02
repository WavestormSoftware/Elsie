using System.Net.Quic;
using System.Threading.Channels;

namespace Elsie.Web.Http3;

/// <summary>
/// Lazy request-body stream for HTTP/3: DATA frames are read from the QUIC stream in the
/// background and surfaced through a bounded channel, so handlers can stream request bodies
/// without waiting for the client to finish sending. Total size is capped at
/// <c>MaxRequestBodyBytes</c> (overflow marks the stream failed → 413 by the host).
/// </summary>
internal sealed class QuicRequestBodyStream : Stream
{
    private readonly Channel<byte[]> _frames;
    private readonly Stream _stream;
    private readonly long _maxBody;
    private long _readTotal;
    private bool _tooLarge;
    // Unconsumed remainder of a frame that did not fit the caller's buffer. Served FIRST on
    // the next read — never pushed back onto the channel (push-back reordered bytes behind
    // later frames and violated the channel's single-writer contract, dropping data).
    private byte[]? _pending;
    private int _pendingOffset;
    private int _pendingLength;

    public QuicRequestBodyStream(Stream stream, long maxBody)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _maxBody = maxBody > 0 ? maxBody : long.MaxValue;
        _frames = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });
    }

    /// <summary>Starts the background DATA-frame pump (call once after construction).</summary>
    public void StartReadingAsync(CancellationToken cancellationToken)
    {
        _ = PumpAsync(cancellationToken);
    }

    private async Task PumpAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var frame = await Http3FrameReader.ReadAsync(_stream, cancellationToken).ConfigureAwait(false);
                if (frame is null)
                {
                    break; // stream FIN
                }

                if (frame.Value.Type == Http3FrameType.Data)
                {
                    var payload = frame.Value.Payload;
                    if (_readTotal + payload.Length > _maxBody)
                    {
                        _tooLarge = true;
                        break;
                    }

                    _readTotal += payload.Length;
                    await _frames.Writer.WriteAsync(payload.ToArray(), cancellationToken).ConfigureAwait(false);
                }
                // HEADERS frames after DATA are response trailers — ignored for the request side.
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Client aborted the stream — surface end-of-body.
        }

        _frames.Writer.TryComplete();
    }

    /// <summary>True when the body exceeded the configured maximum (caller should 413).</summary>
    public bool IsTooLarge => _tooLarge;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => _readTotal;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken = default)
    {
        // Serve the unconsumed remainder of a split frame first; only refill from the channel
        // when nothing is pending. A frame larger than the caller's buffer (e.g. the framework
        // CopyToAsync 81920-byte reads or gRPC's 5-byte header reads) must never be reordered
        // behind frames that arrived later.
        while (_pending is null || _pendingOffset >= _pendingLength)
        {
            _pending = null;
            if (!await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0; // end of body
            }

            if (_frames.Reader.TryRead(out var chunk))
            {
                _pending = chunk;
                _pendingOffset = 0;
                _pendingLength = chunk.Length;
            }
        }

        var take = Math.Min(buffer.Length, _pendingLength - _pendingOffset);
        _pending.AsMemory(_pendingOffset, take).CopyTo(buffer);
        _pendingOffset += take;
        return take;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
