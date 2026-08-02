namespace Elsie.Web.Http2;

/// <summary>
/// Stream adapter that turns writes into HTTP/2 DATA frames (split by the peer's max frame
/// size), so unknown-length <c>BodyWriter</c> responses (SSE, static files, gRPC) stream
/// incrementally instead of being buffered.
/// </summary>
internal sealed class Http2ResponseBodyStream : Stream
{
    private readonly Http2Connection _connection;
    private readonly int _streamId;
    private readonly int _maxFrameSize;
    private readonly CancellationToken _cancellationToken;

    public Http2ResponseBodyStream(
        Http2Connection connection,
        int streamId,
        int maxFrameSize,
        CancellationToken cancellationToken)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _streamId = streamId;
        _maxFrameSize = maxFrameSize > 0 ? maxFrameSize : 16384;
        _cancellationToken = cancellationToken;
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (buffer.IsEmpty)
        {
            return;
        }

        var token = cancellationToken.CanBeCanceled ? cancellationToken : _cancellationToken;
        var offset = 0;
        while (offset < buffer.Length)
        {
            var take = Math.Min(_maxFrameSize, buffer.Length - offset);
            await _connection.WriteDataFrameAsync(_streamId, buffer.Slice(offset, take), token)
                .ConfigureAwait(false);
            offset += take;
        }
    }

    public override void Flush() { }
    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
