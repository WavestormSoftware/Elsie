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
    private readonly QuicStream _quicStream;
    private readonly long _maxBody;
    private long _readTotal;
    private bool _tooLarge;

    public QuicRequestBodyStream(QuicStream quicStream, long maxBody)
    {
        _quicStream = quicStream ?? throw new ArgumentNullException(nameof(quicStream));
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
                var frame = await Http3FrameReader.ReadAsync(_quicStream, cancellationToken).ConfigureAwait(false);
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
        while (await _frames.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_frames.Reader.TryRead(out var chunk))
            {
                var take = Math.Min(chunk.Length, buffer.Length);
                chunk.AsMemory(0, take).CopyTo(buffer);
                if (take < chunk.Length)
                {
                    // Refill path: put the remainder back (rare; frames are small).
                    _frames.Writer.TryWrite(chunk[take..]);
                }

                return take;
            }
        }

        return 0; // end of body
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
